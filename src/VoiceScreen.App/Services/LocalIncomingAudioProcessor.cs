using System.IO;
using System.Net.Http;
using System.Threading.Channels;
using VoiceScreen.App.Diagnostics;

namespace VoiceScreen.App.Services;

/// <summary>
/// 将 Discord 的 40ms PCM16 帧按语音活动切成句子，再交给本地 Whisper + OPUS-MT。
/// 这里使用数字音频能量检测；Discord 进程无输出时 Process Loopback 返回真正的静音帧。
/// </summary>
public sealed class LocalIncomingAudioProcessor : IAsyncDisposable
{
    private const int FrameBytes = 1280; // 16kHz * 40ms * PCM16 mono
    private const int VoiceRmsThreshold = 120;
    private const int StartVoiceFrames = 2;
    private const int EndSilenceFrames = 50; // 2000ms，保留真实 Discord 对话里的思考停顿
    private const int PreRollFrames = 8; // 320ms，避免吞掉句首辅音
    private const int MinimumVoicedFrames = 3;
    private const int MaximumUtteranceFrames = 500; // 20s，限制单次请求规模，降低长请求断连和后续字幕积压

    private readonly object _gate = new();
    private readonly LocalOutgoingService _localService;
    private readonly Queue<byte[]> _preRoll = new();
    private readonly Channel<byte[]> _utterances = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(12)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest
    });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _worker;
    private MemoryStream? _current;
    private int _consecutiveVoiceFrames;
    private int _silenceFrames;
    private int _voicedFrames;
    private int _totalFrames;

    public LocalIncomingAudioProcessor(LocalOutgoingService localService)
    {
        _localService = localService;
        _worker = Task.Run(ProcessUtterancesAsync);
    }

    public event EventHandler<LocalIncomingTranslation>? TranslationReady;
    public event EventHandler<string>? Error;
    public event EventHandler<string>? Status;

    public ValueTask AddFrameAsync(byte[] frame, bool acceptIncoming, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (frame.Length != FrameBytes) return ValueTask.CompletedTask;

        lock (_gate)
        {
            if (!acceptIncoming)
            {
                ResetNoLock();
                return ValueTask.CompletedTask;
            }

            var voiced = CalculateRms(frame) >= VoiceRmsThreshold;
            if (_current is null)
            {
                AddPreRoll(frame);
                _consecutiveVoiceFrames = voiced ? _consecutiveVoiceFrames + 1 : 0;
                if (_consecutiveVoiceFrames >= StartVoiceFrames)
                {
                    _current = new MemoryStream(FrameBytes * 100);
                    foreach (var bufferedFrame in _preRoll)
                        _current.Write(bufferedFrame, 0, bufferedFrame.Length);
                    _totalFrames = _preRoll.Count;
                    _voicedFrames = _consecutiveVoiceFrames;
                    _silenceFrames = 0;
                    _preRoll.Clear();
                }
                return ValueTask.CompletedTask;
            }

            _current.Write(frame, 0, frame.Length);
            _totalFrames++;
            if (voiced)
            {
                _voicedFrames++;
                _silenceFrames = 0;
            }
            else
            {
                _silenceFrames++;
            }

            if (_silenceFrames >= EndSilenceFrames || _totalFrames >= MaximumUtteranceFrames)
                CompleteUtteranceNoLock();
        }
        return ValueTask.CompletedTask;
    }

    public void Reset()
    {
        lock (_gate) ResetNoLock();
    }

    private void CompleteUtteranceNoLock()
    {
        var stream = _current;
        var shouldProcess = stream is not null && _voicedFrames >= MinimumVoicedFrames;
        var audio = shouldProcess ? stream!.ToArray() : null;
        ResetNoLock();
        if (audio is not null && audio.Length > 0)
            _utterances.Writer.TryWrite(audio);
    }

    private void ResetNoLock()
    {
        _current?.Dispose();
        _current = null;
        _preRoll.Clear();
        _consecutiveVoiceFrames = 0;
        _silenceFrames = 0;
        _voicedFrames = 0;
        _totalFrames = 0;
    }

    private void AddPreRoll(byte[] frame)
    {
        _preRoll.Enqueue((byte[])frame.Clone());
        while (_preRoll.Count > PreRollFrames) _preRoll.Dequeue();
    }

    private async Task ProcessUtterancesAsync()
    {
        try
        {
            await foreach (var audio in _utterances.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    // Total CPU usage can remain low while an individual inference request is still delayed.
                    // Leave enough time for Whisper and bridged translation without keeping a stalled request forever.
                    timeout.CancelAfter(TimeSpan.FromSeconds(90));
                    var result = await _localService.TranslateIncomingSpeechAsync(audio, timeout.Token)
                        .ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(result.SourceText) && !string.IsNullOrWhiteSpace(result.TranslatedText))
                        TranslationReady?.Invoke(this, result);
                }
                catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
                {
                    VoiceScreenLog.Warn("Local incoming translation exceeded 90 seconds; segment skipped");
                    Status?.Invoke(this, "本地模型繁忙，本段已跳过，后续 Discord 字幕会自动继续");
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    VoiceScreenLog.Error("Local incoming translation failed", ex);
                    if (ex is HttpRequestException)
                        Status?.Invoke(this, "本地模型连接短暂中断，本段已跳过，后续字幕会自动继续");
                    else
                        Error?.Invoke(this, ex.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
    }

    private static double CalculateRms(byte[] pcm)
    {
        double sum = 0;
        var sampleCount = pcm.Length / 2;
        for (var i = 0; i < pcm.Length; i += 2)
        {
            var sample = (short)(pcm[i] | (pcm[i + 1] << 8));
            sum += (double)sample * sample;
        }
        return Math.Sqrt(sum / sampleCount);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate) ResetNoLock();
        _utterances.Writer.TryComplete();
        _lifetime.Cancel();
        try { await _worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _lifetime.Dispose();
    }
}
