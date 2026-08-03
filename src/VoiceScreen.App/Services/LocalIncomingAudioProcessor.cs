using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Channels;
using VoiceScreen.App.Diagnostics;

namespace VoiceScreen.App.Services;

/// <summary>
/// 将 Discord 40ms PCM 帧切成语音会话。稳定模式在句末处理一次；低延迟模式会产生音频快照，
/// ASR 与 OPUS 分别在独立流水线上运行，只保留有价值的最新临时结果，最终句永不丢弃。
/// </summary>
public sealed class LocalIncomingAudioProcessor : IAsyncDisposable
{
    private const int FrameBytes = 1280; // 16kHz * 40ms * PCM16 mono
    private const int VoiceRmsThreshold = 120;
    private const int StartVoiceFrames = 2;
    private const int StableEndSilenceFrames = 50; // 2000ms
    private const int RealtimeEndSilenceFrames = 16; // 640ms
    private const int RealtimeFirstSnapshotFrames = 25; // 1000ms
    private const int RealtimeSnapshotIntervalFrames = 15; // 600ms
    private const int PreRollFrames = 8; // 320ms
    private const int MinimumVoicedFrames = 3;
    private const int MaximumUtteranceFrames = 500; // 20s

    private readonly object _gate = new();
    private readonly object _previewGate = new();
    private readonly LocalOutgoingService _localService;
    private readonly bool _lowLatency;
    private readonly Queue<byte[]> _preRoll = new();
    private readonly Channel<AudioSnapshot> _audioSnapshots = Channel.CreateUnbounded<AudioSnapshot>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Channel<TranslationSnapshot> _translationSnapshots = Channel.CreateUnbounded<TranslationSnapshot>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly ConcurrentDictionary<long, int> _latestAudioRevision = new();
    private readonly ConcurrentDictionary<long, int> _latestTranslationRevision = new();
    private readonly ConcurrentDictionary<long, int> _finalAudioRevision = new();
    private readonly ConcurrentDictionary<long, int> _finalTranslationRevision = new();
    private readonly ConcurrentDictionary<long, string> _previousHypotheses = new();
    private readonly ConcurrentDictionary<long, string> _lastStableSources = new();
    private readonly ConcurrentDictionary<long, string> _lastTranslations = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _asrWorker;
    private readonly Task _translationWorker;
    private MemoryStream? _current;
    private long _utteranceStartTimestamp;
    private long _previewUtteranceId;
    private bool _previewIsVisible;
    private long _utteranceId;
    private int _revision;
    private int _lastSnapshotFrame;
    private int _consecutiveVoiceFrames;
    private int _silenceFrames;
    private int _voicedFrames;
    private int _totalFrames;

    public LocalIncomingAudioProcessor(LocalOutgoingService localService, bool lowLatency = false)
    {
        _localService = localService;
        _lowLatency = lowLatency;
        _asrWorker = Task.Run(ProcessAudioSnapshotsAsync);
        _translationWorker = Task.Run(ProcessTranslationSnapshotsAsync);
    }

    public event EventHandler<LocalIncomingTranslation>? TranslationReady;
    public event EventHandler<LocalIncomingTranslation?>? PreviewChanged;
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
                ResetNoLock(clearPreview: true);
                return ValueTask.CompletedTask;
            }

            var voiced = CalculateRms(frame) >= VoiceRmsThreshold;
            if (_current is null)
            {
                AddPreRoll(frame);
                _consecutiveVoiceFrames = voiced ? _consecutiveVoiceFrames + 1 : 0;
                if (_consecutiveVoiceFrames >= StartVoiceFrames)
                    StartUtteranceNoLock();
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

            if (_lowLatency && _totalFrames >= RealtimeFirstSnapshotFrames
                && _totalFrames - _lastSnapshotFrame >= RealtimeSnapshotIntervalFrames
                && _silenceFrames < RealtimeEndSilenceFrames)
                QueueSnapshotNoLock(isFinal: false);

            var endSilenceFrames = _lowLatency ? RealtimeEndSilenceFrames : StableEndSilenceFrames;
            if (_silenceFrames >= endSilenceFrames || _totalFrames >= MaximumUtteranceFrames)
                CompleteUtteranceNoLock();
        }
        return ValueTask.CompletedTask;
    }

    public void Reset()
    {
        lock (_gate) ResetNoLock(clearPreview: true);
    }

    private void StartUtteranceNoLock()
    {
        _current = new MemoryStream(FrameBytes * 100);
        foreach (var bufferedFrame in _preRoll)
            _current.Write(bufferedFrame, 0, bufferedFrame.Length);
        _utteranceId++;
        _utteranceStartTimestamp = Stopwatch.GetTimestamp();
        _revision = 0;
        _totalFrames = _preRoll.Count;
        _lastSnapshotFrame = 0;
        _voicedFrames = _consecutiveVoiceFrames;
        _silenceFrames = 0;
        _preRoll.Clear();
    }

    private void CompleteUtteranceNoLock()
    {
        if (_current is not null && _voicedFrames >= MinimumVoicedFrames)
            QueueSnapshotNoLock(isFinal: true);
        ResetNoLock(clearPreview: false);
    }

    private void QueueSnapshotNoLock(bool isFinal)
    {
        if (_current is null || _current.Length == 0) return;
        var revision = ++_revision;
        _lastSnapshotFrame = _totalFrames;
        var snapshot = new AudioSnapshot(_utteranceId, revision, _current.ToArray(), isFinal,
            _utteranceStartTimestamp);
        _latestAudioRevision[_utteranceId] = revision;
        if (isFinal) _finalAudioRevision[_utteranceId] = revision;
        _audioSnapshots.Writer.TryWrite(snapshot);
    }

    private void ResetNoLock(bool clearPreview)
    {
        _current?.Dispose();
        _current = null;
        _preRoll.Clear();
        _consecutiveVoiceFrames = 0;
        _silenceFrames = 0;
        _voicedFrames = 0;
        _totalFrames = 0;
        _revision = 0;
        _lastSnapshotFrame = 0;
        if (clearPreview) ClearPreview();
    }

    private void AddPreRoll(byte[] frame)
    {
        _preRoll.Enqueue((byte[])frame.Clone());
        while (_preRoll.Count > PreRollFrames) _preRoll.Dequeue();
    }

    private async Task ProcessAudioSnapshotsAsync()
    {
        try
        {
            await foreach (var snapshot in _audioSnapshots.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (IsStale(snapshot.UtteranceId, snapshot.Revision, snapshot.IsFinal, _latestAudioRevision))
                    continue;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    timeout.CancelAfter(snapshot.IsFinal ? TimeSpan.FromSeconds(90) : TimeSpan.FromSeconds(30));
                    var transcription = await _localService.TranscribeIncomingSpeechAsync(snapshot.Audio, timeout.Token,
                            preview: !snapshot.IsFinal)
                        .ConfigureAwait(false);
                    if (IsSupersededByFinal(snapshot.UtteranceId, snapshot.IsFinal, _finalAudioRevision))
                        continue;
                    if (string.IsNullOrWhiteSpace(transcription.Text)
                        || LocalOutgoingService.IsLikelyIncomingHallucination(transcription.Text, transcription.Language))
                    {
                        if (snapshot.IsFinal) FinishUtterance(snapshot.UtteranceId);
                        continue;
                    }

                    var lastTranslation = _lastTranslations.GetValueOrDefault(snapshot.UtteranceId, string.Empty);
                    SetPreview(snapshot.UtteranceId, new LocalIncomingTranslation(transcription.Text, lastTranslation,
                        NormalizeLanguage(transcription.Text, transcription.Language)));

                    var sourceForTranslation = SelectStableSource(snapshot, transcription);
                    if (string.IsNullOrWhiteSpace(sourceForTranslation) && !snapshot.IsFinal) continue;
                    sourceForTranslation = snapshot.IsFinal ? transcription.Text : sourceForTranslation;

                    _latestTranslationRevision[snapshot.UtteranceId] = snapshot.Revision;
                    if (snapshot.IsFinal) _finalTranslationRevision[snapshot.UtteranceId] = snapshot.Revision;
                    _translationSnapshots.Writer.TryWrite(new TranslationSnapshot(snapshot.UtteranceId,
                        snapshot.Revision, transcription.Text, sourceForTranslation, transcription.Language,
                        snapshot.IsFinal, snapshot.StartTimestamp));
                }
                catch (Exception ex) when (HandleWorkerException(ex, snapshot.IsFinal, "ASR"))
                {
                    if (snapshot.IsFinal) FinishUtterance(snapshot.UtteranceId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
    }

    private async Task ProcessTranslationSnapshotsAsync()
    {
        try
        {
            await foreach (var snapshot in _translationSnapshots.Reader.ReadAllAsync(_lifetime.Token)
                               .ConfigureAwait(false))
            {
                if (IsStale(snapshot.UtteranceId, snapshot.Revision, snapshot.IsFinal,
                        _latestTranslationRevision))
                    continue;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    timeout.CancelAfter(snapshot.IsFinal ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(20));
                    var translated = await _localService.TranslateIncomingTextAsync(snapshot.SourceForTranslation,
                        snapshot.Language, timeout.Token).ConfigureAwait(false);
                    if (IsSupersededByFinal(snapshot.UtteranceId, snapshot.IsFinal, _finalTranslationRevision))
                        continue;
                    if (string.IsNullOrWhiteSpace(translated.TranslatedText))
                    {
                        if (snapshot.IsFinal) FinishUtterance(snapshot.UtteranceId);
                        continue;
                    }

                    var display = new LocalIncomingTranslation(snapshot.DisplaySource, translated.TranslatedText,
                        translated.Language);
                    if (snapshot.IsFinal)
                    {
                        TranslationReady?.Invoke(this, display);
                        FinishUtterance(snapshot.UtteranceId);
                        VoiceScreenLog.Info($"Realtime final subtitle latency={ElapsedMilliseconds(snapshot.StartTimestamp)}ms");
                    }
                    else
                    {
                        _lastTranslations[snapshot.UtteranceId] = translated.TranslatedText;
                        SetPreview(snapshot.UtteranceId, display);
                        VoiceScreenLog.Info($"Realtime partial subtitle latency={ElapsedMilliseconds(snapshot.StartTimestamp)}ms");
                    }
                }
                catch (Exception ex) when (HandleWorkerException(ex, snapshot.IsFinal, "translation"))
                {
                    if (snapshot.IsFinal) FinishUtterance(snapshot.UtteranceId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
    }

    private string SelectStableSource(AudioSnapshot snapshot, LocalTranscription current)
    {
        if (snapshot.IsFinal) return current.Text;
        if (!_previousHypotheses.TryGetValue(snapshot.UtteranceId, out var previous))
        {
            _previousHypotheses[snapshot.UtteranceId] = current.Text;
            return EndsClause(current.Text) ? current.Text : string.Empty;
        }

        _previousHypotheses[snapshot.UtteranceId] = current.Text;
        var stable = LongestStablePrefix(previous, current.Text, current.Language);
        if (stable.Length < MinimumStableLength(current.Language)) return string.Empty;
        if (_lastStableSources.TryGetValue(snapshot.UtteranceId, out var last) && stable == last)
            return string.Empty;
        _lastStableSources[snapshot.UtteranceId] = stable;
        return stable;
    }

    private static string LongestStablePrefix(string previous, string current, string language)
    {
        var length = Math.Min(previous.Length, current.Length);
        var index = 0;
        while (index < length && char.ToUpperInvariant(previous[index]) == char.ToUpperInvariant(current[index]))
            index++;
        if (index == 0) return string.Empty;
        var prefix = current[..index].TrimEnd();
        if (language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            var boundary = prefix.LastIndexOfAny([' ', ',', '.', '!', '?', ';', ':']);
            if (boundary > 0) prefix = prefix[..boundary].TrimEnd();
        }
        return prefix;
    }

    private static int MinimumStableLength(string language)
        => language.StartsWith("th", StringComparison.OrdinalIgnoreCase) ? 4 : 3;

    private static bool EndsClause(string text)
        => text.EndsWith('.') || text.EndsWith('!') || text.EndsWith('?')
           || text.EndsWith('。') || text.EndsWith('！') || text.EndsWith('？');

    private static string NormalizeLanguage(string text, string detectedLanguage)
    {
        if (text.Any(character => character is >= '\u3400' and <= '\u9fff')) return "zh";
        if (text.Any(character => character is >= '\u0e00' and <= '\u0e7f')) return "th";
        return detectedLanguage;
    }

    private static bool IsStale(long utteranceId, int revision, bool isFinal,
        ConcurrentDictionary<long, int> latest)
        => !isFinal && latest.TryGetValue(utteranceId, out var newest) && revision < newest;

    private static bool IsSupersededByFinal(long utteranceId, bool isFinal,
        ConcurrentDictionary<long, int> finals)
        => !isFinal && finals.ContainsKey(utteranceId);

    private bool HandleWorkerException(Exception ex, bool isFinal, string stage)
    {
        if (ex is OperationCanceledException && _lifetime.IsCancellationRequested) return false;
        if (ex is OperationCanceledException)
        {
            VoiceScreenLog.Warn($"Local incoming realtime {stage} timed out; snapshot skipped");
            if (isFinal) Status?.Invoke(this, "本地模型繁忙，本句已跳过，后续 Discord 字幕会自动继续");
            return true;
        }
        VoiceScreenLog.Error($"Local incoming realtime {stage} failed", ex);
        if (ex is HttpRequestException)
            Status?.Invoke(this, "本地模型连接短暂中断，后续字幕会自动继续");
        else if (isFinal)
            Error?.Invoke(this, ex.Message);
        return true;
    }

    private void FinishUtterance(long utteranceId)
    {
        SetPreview(utteranceId, null);
        _latestAudioRevision.TryRemove(utteranceId, out _);
        _latestTranslationRevision.TryRemove(utteranceId, out _);
        _finalAudioRevision.TryRemove(utteranceId, out _);
        _finalTranslationRevision.TryRemove(utteranceId, out _);
        _previousHypotheses.TryRemove(utteranceId, out _);
        _lastStableSources.TryRemove(utteranceId, out _);
        _lastTranslations.TryRemove(utteranceId, out _);
    }

    private void SetPreview(long utteranceId, LocalIncomingTranslation? preview)
    {
        var raise = false;
        lock (_previewGate)
        {
            if (preview is null)
            {
                if (!_previewIsVisible || _previewUtteranceId != utteranceId) return;
                _previewIsVisible = false;
                _previewUtteranceId = 0;
                raise = true;
            }
            else
            {
                _previewIsVisible = true;
                _previewUtteranceId = utteranceId;
                raise = true;
            }
        }
        if (raise) PreviewChanged?.Invoke(this, preview);
    }

    private void ClearPreview()
    {
        var raise = false;
        lock (_previewGate)
        {
            if (_previewIsVisible)
            {
                _previewIsVisible = false;
                _previewUtteranceId = 0;
                raise = true;
            }
        }
        if (raise) PreviewChanged?.Invoke(this, null);
    }

    private static long ElapsedMilliseconds(long startTimestamp)
        => (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

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
        lock (_gate) ResetNoLock(clearPreview: true);
        _audioSnapshots.Writer.TryComplete();
        _translationSnapshots.Writer.TryComplete();
        _lifetime.Cancel();
        try { await Task.WhenAll(_asrWorker, _translationWorker).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _lifetime.Dispose();
    }

    private sealed record AudioSnapshot(long UtteranceId, int Revision, byte[] Audio, bool IsFinal,
        long StartTimestamp);

    private sealed record TranslationSnapshot(long UtteranceId, int Revision, string DisplaySource,
        string SourceForTranslation, string Language, bool IsFinal, long StartTimestamp);
}
