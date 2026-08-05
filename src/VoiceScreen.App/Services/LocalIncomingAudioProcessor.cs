using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Channels;
using VoiceScreen.App.Diagnostics;
using VoiceScreen.Core;

namespace VoiceScreen.App.Services;

/// <summary>
/// 将 Discord 40ms PCM 帧切成语音会话。稳定模式在句末处理一次；低延迟模式会产生音频快照，
/// ASR 与 OPUS 分别在独立流水线上运行，只保留有价值的最新临时结果，最终句永不丢弃。
///
/// 分配策略：这条链路上的音频回调每 40ms 就要跑一次，任何每帧分配都会变成常驻 GC 压力。
/// 因此预滚缓冲用固定数组环复用，整句缓冲整个生命周期只分配一次，快照走 ArrayPool。
/// </summary>
public sealed class LocalIncomingAudioProcessor : IIncomingAudioProcessor
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
    private readonly Func<string, string, CancellationToken, Task<LocalIncomingTranslation>>? _translateOverride;

    // 预滚环形缓冲：数组只在首次用到时分配一次，之后按帧覆盖写入。
    // 之前这里是 Queue<byte[]> + frame.Clone()，每秒 25 次 1280 字节分配，全程常驻。
    private readonly byte[][] _preRollFrames = new byte[PreRollFrames][];
    private int _preRollCount;
    private int _preRollHead;

    // 整句缓冲：按最长一句（20s + 预滚）一次性分配，靠 SetLength(0) 复用，
    // 不再每句 new 一个 MemoryStream 再 Dispose。
    private readonly MemoryStream _utterance = new(FrameBytes * (MaximumUtteranceFrames + PreRollFrames));
    private bool _utteranceActive;

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

    public LocalIncomingAudioProcessor(LocalOutgoingService localService, bool lowLatency = false,
        Func<string, string, CancellationToken, Task<LocalIncomingTranslation>>? translateOverride = null)
    {
        _localService = localService;
        _lowLatency = lowLatency;
        _translateOverride = translateOverride;
        _asrWorker = Task.Run(ProcessAudioSnapshotsAsync);
        _translationWorker = Task.Run(ProcessTranslationSnapshotsAsync);
    }

    public event EventHandler<LocalIncomingTranslation>? TranslationReady;
    public event EventHandler<LocalIncomingTranslation?>? PreviewChanged;
    public event EventHandler<string>? Error;
    public event EventHandler<string>? Status;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

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

            var voiced = PcmLevel.CalculateRms(frame) >= VoiceRmsThreshold;
            if (!_utteranceActive)
            {
                AddPreRollNoLock(frame);
                _consecutiveVoiceFrames = voiced ? _consecutiveVoiceFrames + 1 : 0;
                if (_consecutiveVoiceFrames >= StartVoiceFrames)
                    StartUtteranceNoLock();
                return ValueTask.CompletedTask;
            }

            _utterance.Write(frame, 0, frame.Length);
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
        _utterance.SetLength(0);
        _utteranceActive = true;

        // 预滚按写入顺序回放，保证句首的辅音不被 RMS 门限吃掉。
        for (var offset = 0; offset < _preRollCount; offset++)
        {
            var buffered = _preRollFrames[(_preRollHead + offset) % PreRollFrames];
            _utterance.Write(buffered, 0, FrameBytes);
        }

        _utteranceId++;
        _utteranceStartTimestamp = Stopwatch.GetTimestamp();
        _revision = 0;
        _totalFrames = _preRollCount;
        _lastSnapshotFrame = 0;
        _voicedFrames = _consecutiveVoiceFrames;
        _silenceFrames = 0;
        _preRollCount = 0;
        _preRollHead = 0;
    }

    private void CompleteUtteranceNoLock()
    {
        if (_utteranceActive && _voicedFrames >= MinimumVoicedFrames)
            QueueSnapshotNoLock(isFinal: true);
        ResetNoLock(clearPreview: false);
    }

    /// <summary>
    /// 从池里租一段刚好够用的缓冲拷走当前音频。租用的数组由 <see cref="ProcessAudioSnapshotsAsync"/>
    /// 统一归还——快照被判定过期而丢弃时也要还，否则池会被慢慢掏空。
    /// </summary>
    private void QueueSnapshotNoLock(bool isFinal)
    {
        if (!_utteranceActive || _utterance.Length == 0) return;
        var length = (int)_utterance.Length;
        var revision = ++_revision;
        _lastSnapshotFrame = _totalFrames;

        var rented = ArrayPool<byte>.Shared.Rent(length);
        Buffer.BlockCopy(_utterance.GetBuffer(), 0, rented, 0, length);

        var snapshot = new AudioSnapshot(_utteranceId, revision, rented, length, isFinal, _utteranceStartTimestamp);
        _latestAudioRevision[_utteranceId] = revision;
        if (isFinal) _finalAudioRevision[_utteranceId] = revision;
        if (!_audioSnapshots.Writer.TryWrite(snapshot))
            ArrayPool<byte>.Shared.Return(rented);
    }

    private void ResetNoLock(bool clearPreview)
    {
        _utterance.SetLength(0);
        _utteranceActive = false;
        _preRollCount = 0;
        _preRollHead = 0;
        _consecutiveVoiceFrames = 0;
        _silenceFrames = 0;
        _voicedFrames = 0;
        _totalFrames = 0;
        _revision = 0;
        _lastSnapshotFrame = 0;
        if (clearPreview) ClearPreview();
    }

    private void AddPreRollNoLock(byte[] frame)
    {
        int slot;
        if (_preRollCount == PreRollFrames)
        {
            // 环已满：覆盖最旧的一帧，并把队首往前推。
            slot = _preRollHead;
            _preRollHead = (_preRollHead + 1) % PreRollFrames;
        }
        else
        {
            slot = (_preRollHead + _preRollCount) % PreRollFrames;
            _preRollCount++;
        }

        _preRollFrames[slot] ??= new byte[FrameBytes];
        Buffer.BlockCopy(frame, 0, _preRollFrames[slot], 0, FrameBytes);
    }

    private async Task ProcessAudioSnapshotsAsync()
    {
        try
        {
            await foreach (var snapshot in _audioSnapshots.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                try
                {
                    if (IsStale(snapshot.UtteranceId, snapshot.Revision, snapshot.IsFinal, _latestAudioRevision))
                        continue;
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                        timeout.CancelAfter(snapshot.IsFinal ? TimeSpan.FromSeconds(90) : TimeSpan.FromSeconds(30));
                        var transcription = await _localService.TranscribeIncomingSpeechAsync(
                                snapshot.Buffer.AsMemory(0, snapshot.Length), timeout.Token,
                                preview: !snapshot.IsFinal)
                            .ConfigureAwait(false);
                        if (IsSupersededByFinal(snapshot.UtteranceId, snapshot.IsFinal, _finalAudioRevision))
                            continue;
                        if (string.IsNullOrWhiteSpace(transcription.Text)
                            || TranscriptSanitizer.IsPathologicalRepetition(transcription.Text))
                        {
                            if (snapshot.IsFinal) FinishUtterance(snapshot.UtteranceId);
                            continue;
                        }

                        var lastTranslation = _lastTranslations.GetValueOrDefault(snapshot.UtteranceId, string.Empty);
                        SetPreview(snapshot.UtteranceId, new LocalIncomingTranslation(transcription.Text,
                            lastTranslation, SpokenLanguage.Detect(transcription.Text, transcription.Language)));

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
                finally
                {
                    ArrayPool<byte>.Shared.Return(snapshot.Buffer);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
        finally
        {
            DrainPendingAudioSnapshots();
        }
    }

    /// <summary>停止时把还排在队里的快照全部归还给池。</summary>
    private void DrainPendingAudioSnapshots()
    {
        while (_audioSnapshots.Reader.TryRead(out var pending))
            ArrayPool<byte>.Shared.Return(pending.Buffer);
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
                    var translated = _translateOverride is null
                        ? await _localService.TranslateIncomingTextAsync(snapshot.SourceForTranslation,
                            snapshot.Language, timeout.Token).ConfigureAwait(false)
                        : await _translateOverride(snapshot.SourceForTranslation, snapshot.Language, timeout.Token)
                            .ConfigureAwait(false);
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
            return IncrementalTranscript.EndsClause(current.Text) ? current.Text : string.Empty;
        }

        _previousHypotheses[snapshot.UtteranceId] = current.Text;
        var stable = IncrementalTranscript.LongestStablePrefix(previous, current.Text, current.Language);
        if (stable.Length < IncrementalTranscript.MinimumStableLength(current.Language)) return string.Empty;
        if (_lastStableSources.TryGetValue(snapshot.UtteranceId, out var last) && stable == last)
            return string.Empty;
        _lastStableSources[snapshot.UtteranceId] = stable;
        return stable;
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

    public async ValueTask DisposeAsync()
    {
        lock (_gate) ResetNoLock(clearPreview: true);
        _audioSnapshots.Writer.TryComplete();
        _translationSnapshots.Writer.TryComplete();
        _lifetime.Cancel();
        try { await Task.WhenAll(_asrWorker, _translationWorker).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        DrainPendingAudioSnapshots();
        _utterance.Dispose();
        _lifetime.Dispose();
    }

    /// <summary>
    /// <paramref name="Buffer"/> 来自 <see cref="ArrayPool{T}"/>，长度可能大于
    /// <paramref name="Length"/>；只有前 <paramref name="Length"/> 字节是有效音频。
    /// </summary>
    private sealed record AudioSnapshot(long UtteranceId, int Revision, byte[] Buffer, int Length, bool IsFinal,
        long StartTimestamp);

    private sealed record TranslationSnapshot(long UtteranceId, int Revision, string DisplaySource,
        string SourceForTranslation, string Language, bool IsFinal, long StartTimestamp);
}
