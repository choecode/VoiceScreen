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
/// 滚动窗口：Whisper 是无状态的，每次临时识别都要重送一段音频。如果每次都从句首重送，
/// 计算量随句子长度平方增长——说到第 10 秒时每次都在重算那 10 秒。因此每当
/// LocalAgreement 确认了一段前缀，就连同它对应的那段音频一起从缓冲里删掉
/// （<see cref="TranscriptWindow.CommittedEndSeconds"/> 用词级时间戳定位裁剪点），
/// 窗口长度因此保持恒定，长句和短句的临时字幕延迟一样。
/// Sherpa 走另一条路：解码状态留在服务端会话里，客户端每次只送新到的那几百毫秒，
/// 天然就是增量的，不需要也不能做裁剪。
///
/// 分配策略：这条链路上的音频回调每 40ms 就要跑一次，任何每帧分配都会变成常驻 GC 压力。
/// 因此预滚缓冲用固定数组环复用，整句缓冲整个生命周期只分配一次，快照走 ArrayPool。
/// </summary>
public sealed class LocalIncomingAudioProcessor : IIncomingAudioProcessor
{
    private const int FrameBytes = 1280; // 16kHz * 40ms * PCM16 mono
    private const int BytesPerSecond = 32000; // 16kHz * PCM16 mono
    private const int VoiceRmsThreshold = 120;
    private const int StartVoiceFrames = 2;
    private const int StableEndSilenceFrames = 50; // 2000ms
    private const int RealtimeEndSilenceFrames = 16; // 640ms
    private const int RealtimeFirstSnapshotFrames = 25; // 1000ms
    private const int RealtimeSnapshotIntervalFrames = 15; // 600ms
    private const int PreRollFrames = 8; // 320ms
    private const int MinimumVoicedFrames = 3;

    /// <summary>
    /// 送进模型的滚动窗口上限。裁剪正常工作时窗口远达不到这里；一旦识别持续拿不到
    /// 可确认的前缀（例如一直是噪声），这道闸门保证单次识别的耗时仍然有上界。
    /// </summary>
    private const int MaximumWindowFrames = 500; // 20s

    /// <summary>
    /// 一句话的绝对时长上限。有了裁剪，连续说话不再拖慢识别，所以这个上限可以比
    /// 窗口上限宽得多，长段独白不会每 20 秒被硬切一刀。
    /// </summary>
    private const int MaximumUtteranceFrames = 1500; // 60s

    private readonly object _gate = new();
    private readonly object _previewGate = new();
    private readonly LocalOutgoingService _localService;
    private readonly bool _lowLatency;
    private readonly bool _streamingSessions;
    private readonly Func<string, string, int, CancellationToken, Task<LocalIncomingTranslation>>? _translateOverride;

    // 预滚环形缓冲：数组只在首次用到时分配一次，之后按帧覆盖写入。
    // 之前这里是 Queue<byte[]> + frame.Clone()，每秒 25 次 1280 字节分配，全程常驻。
    private readonly byte[][] _preRollFrames = new byte[PreRollFrames][];
    private int _preRollCount;
    private int _preRollHead;

    // 滚动窗口缓冲：按最长窗口一次性分配，靠 SetLength 复用，
    // 不再每句 new 一个 MemoryStream 再 Dispose。
    private readonly MemoryStream _utterance = new(FrameBytes * (MaximumWindowFrames + PreRollFrames));
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
    private readonly ConcurrentDictionary<long, string> _committedTranslations = new();

    /// <summary>流式模式下已经送去翻译过的稳定前缀，用来算出下一段增量。</summary>
    private readonly ConcurrentDictionary<long, string> _committedSources = new();
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

    /// <summary>已确认并已从音频缓冲里裁掉的那段文本。窗口识别结果要接在它后面才是完整一句。</summary>
    private string _committedText = string.Empty;

    /// <summary>
    /// 已经有一个临时快照在识别中。窗口在结果回来之前不能再往前推：否则第二个快照会
    /// 带着尚未裁剪的音频和过期的已确认前缀出发，第一个快照的确认一落地就变成重复文本。
    /// 顺带把「识别跟不上就别再排队」这件事做对了——以前是照发不误，再靠 IsStale 丢掉。
    /// </summary>
    private bool _snapshotInFlight;

    /// <summary>最终快照已经产生。此后任何迟到的确认都不能再改动这一句的窗口。</summary>
    private bool _finalQueued;

    /// <summary>流式模式下，已经送出去过的字节数，下一个快照从这里开始取增量。</summary>
    private int _streamedBytes;

    public LocalIncomingAudioProcessor(LocalOutgoingService localService, bool lowLatency = false,
        Func<string, string, int, CancellationToken, Task<LocalIncomingTranslation>>? translateOverride = null)
    {
        _localService = localService;
        _lowLatency = lowLatency;
        _streamingSessions = localService.UsesStreamingSessions;
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

            if (_lowLatency && !_snapshotInFlight
                && _totalFrames >= RealtimeFirstSnapshotFrames
                && _totalFrames - _lastSnapshotFrame >= RealtimeSnapshotIntervalFrames
                && _silenceFrames < RealtimeEndSilenceFrames)
                QueueSnapshotNoLock(isFinal: false);

            var endSilenceFrames = _lowLatency ? RealtimeEndSilenceFrames : StableEndSilenceFrames;
            if (_silenceFrames >= endSilenceFrames
                || _totalFrames >= MaximumUtteranceFrames
                || WindowFramesNoLock() >= MaximumWindowFrames)
                CompleteUtteranceNoLock();
        }
        return ValueTask.CompletedTask;
    }

    public void Reset()
    {
        lock (_gate) ResetNoLock(clearPreview: true);
    }

    private int WindowFramesNoLock() => (int)(_utterance.Length / FrameBytes);

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
        _committedText = string.Empty;
        _snapshotInFlight = false;
        _finalQueued = false;
        _streamedBytes = 0;
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
    ///
    /// 流式模式只拷走上次之后新增的那一段；Whisper 模式拷走整个滚动窗口。
    /// </summary>
    private void QueueSnapshotNoLock(bool isFinal)
    {
        if (!_utteranceActive || _utterance.Length == 0) return;
        var start = _streamingSessions ? _streamedBytes : 0;
        var length = (int)_utterance.Length - start;
        if (length <= 0) return;

        var revision = ++_revision;
        _lastSnapshotFrame = _totalFrames;
        if (_streamingSessions) _streamedBytes = (int)_utterance.Length;
        if (isFinal) _finalQueued = true;
        else _snapshotInFlight = true;

        var rented = ArrayPool<byte>.Shared.Rent(length);
        Buffer.BlockCopy(_utterance.GetBuffer(), start, rented, 0, length);

        var snapshot = new AudioSnapshot(_utteranceId, revision, rented, length, isFinal, _utteranceStartTimestamp,
            _committedText, IsFirstSnapshot: revision == 1);
        _latestAudioRevision[_utteranceId] = revision;
        if (isFinal) _finalAudioRevision[_utteranceId] = revision;
        if (!_audioSnapshots.Writer.TryWrite(snapshot))
        {
            ArrayPool<byte>.Shared.Return(rented);
            if (!isFinal) _snapshotInFlight = false;
        }
    }

    /// <summary>
    /// 把一段已确认的文本连同它对应的音频从滚动窗口里移出去。
    /// </summary>
    /// <returns>
    /// 是否真的提交了。这一句已经产生最终快照、或者已经换到下一句时返回 false——
    /// 迟到的确认不能再改动一个已经收尾的窗口，否则最终定稿会重复已提交的那一段。
    /// </returns>
    private bool TryCommitWindow(long utteranceId, string stableText, double committedEndSeconds)
    {
        lock (_gate)
        {
            if (!_utteranceActive || _utteranceId != utteranceId || _finalQueued) return false;

            var bytes = (int)(committedEndSeconds * BytesPerSecond);
            bytes -= bytes % 2; // PCM16：必须落在样本边界上，否则剩下的音频整体错位半个样本。
            if (bytes > 0) TrimWindowFrontNoLock(bytes);
            _committedText = TranscriptWindow.Join(_committedText, stableText);
            return true;
        }
    }

    private void TrimWindowFrontNoLock(int bytes)
    {
        var length = (int)_utterance.Length;
        if (bytes >= length)
        {
            _utterance.SetLength(0);
            _utterance.Position = 0;
            return;
        }
        var buffer = _utterance.GetBuffer();
        Buffer.BlockCopy(buffer, bytes, buffer, 0, length - bytes);
        _utterance.SetLength(length - bytes);
        _utterance.Position = length - bytes;
    }

    private void ClearSnapshotInFlight(long utteranceId)
    {
        lock (_gate)
        {
            if (_utteranceId == utteranceId) _snapshotInFlight = false;
        }
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
        _committedText = string.Empty;
        _snapshotInFlight = false;
        _finalQueued = false;
        _streamedBytes = 0;
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
                    // 流式模式送的是增量音频，丢一个快照就等于服务端会话缺一段声音，
                    // 因此过期判定只对「整窗重送」的 Whisper 模式有效。
                    if (!_streamingSessions
                        && IsStale(snapshot.UtteranceId, snapshot.Revision, snapshot.IsFinal, _latestAudioRevision))
                        continue;
                    try
                    {
                        await ProcessAudioSnapshotAsync(snapshot).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (HandleWorkerException(ex, snapshot.IsFinal, "ASR"))
                    {
                        if (snapshot.IsFinal) FinishUtterance(snapshot.UtteranceId);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(snapshot.Buffer);
                    if (!snapshot.IsFinal) ClearSnapshotInFlight(snapshot.UtteranceId);
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

    private async Task ProcessAudioSnapshotAsync(AudioSnapshot snapshot)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(snapshot.IsFinal ? TimeSpan.FromSeconds(90) : TimeSpan.FromSeconds(30));

        var request = new TranscriptionRequest
        {
            Preview = !snapshot.IsFinal,
            // 词级时间戳只用来定位裁剪点：最终快照不再裁剪，流式模式不需要裁剪。
            WantWords = !snapshot.IsFinal && !_streamingSessions,
            Session = _streamingSessions ? snapshot.UtteranceId.ToString() : null,
            ResetSession = snapshot.IsFirstSnapshot
        };
        var transcription = await _localService
            .TranscribeIncomingSpeechAsync(snapshot.Buffer.AsMemory(0, snapshot.Length), request, timeout.Token)
            .ConfigureAwait(false);

        if (IsSupersededByFinal(snapshot.UtteranceId, snapshot.IsFinal, _finalAudioRevision)) return;

        // 完整一句 = 已确认的前缀 + 当前窗口识别出来的尾巴。
        var fullText = TranscriptWindow.Join(snapshot.CommittedText, transcription.Text);
        if (string.IsNullOrWhiteSpace(fullText) || TranscriptSanitizer.IsPathologicalRepetition(fullText))
        {
            if (snapshot.IsFinal) FinishUtterance(snapshot.UtteranceId);
            return;
        }

        var language = SpokenLanguage.Detect(fullText, transcription.Language);
        var committedTranslation = _committedTranslations.GetValueOrDefault(snapshot.UtteranceId, string.Empty);
        SetPreview(snapshot.UtteranceId, new LocalIncomingTranslation(fullText, committedTranslation, language));

        if (snapshot.IsFinal)
        {
            // 定稿重翻整句：临时结果是逐段贪心解码拼出来的，最终这一遍用完整上下文和
            // 更宽的束搜索把它整体替换掉。
            QueueTranslation(snapshot, fullText, fullText, transcription.Language, LocalOutgoingService.FinalBeamSize);
            return;
        }

        var stable = SelectStableSource(snapshot, transcription.Text, transcription.Language);
        if (string.IsNullOrWhiteSpace(stable)) return;

        string chunk;
        if (_streamingSessions)
        {
            // 流式会话返回的是整句累计文本，稳定前缀只会越来越长。直接把它当增量
            // 送去翻译并累加，译文会变成「敌人在二楼敌人在二楼右边」这样的滚雪球。
            // 必须先减掉此前已经翻过的那一段。
            chunk = NewlyStableChunk(snapshot.UtteranceId, stable);
        }
        else
        {
            // Whisper 侧窗口每次提交后都会被裁掉，稳定前缀天然就是增量。
            // 只有真正把这段音频裁掉了才算已确认；提交失败（这一句已经收尾）时
            // 连翻译也不要发，交给最终定稿统一处理。
            var trimSeconds = TranscriptWindow.CommittedEndSeconds(transcription.Words, stable);
            if (!TryCommitWindow(snapshot.UtteranceId, stable, trimSeconds)) return;
            _previousHypotheses[snapshot.UtteranceId] =
                TranscriptWindow.RebasePreviousHypothesis(transcription.Text, stable);
            chunk = stable;
        }

        if (chunk.Length < IncrementalTranscript.MinimumStableLength(transcription.Language)) return;

        // 临时翻译只处理这一次新确认的短语，不再每 600ms 重翻整段。
        QueueTranslation(snapshot, fullText, chunk, transcription.Language,
            LocalOutgoingService.PartialBeamSize);
    }

    private void QueueTranslation(AudioSnapshot snapshot, string displaySource, string sourceForTranslation,
        string language, int beamSize)
    {
        _latestTranslationRevision[snapshot.UtteranceId] = snapshot.Revision;
        if (snapshot.IsFinal) _finalTranslationRevision[snapshot.UtteranceId] = snapshot.Revision;
        _translationSnapshots.Writer.TryWrite(new TranslationSnapshot(snapshot.UtteranceId, snapshot.Revision,
            displaySource, sourceForTranslation, language, snapshot.IsFinal, snapshot.StartTimestamp, beamSize));
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
                            snapshot.Language, snapshot.BeamSize, timeout.Token).ConfigureAwait(false)
                        : await _translateOverride(snapshot.SourceForTranslation, snapshot.Language,
                            snapshot.BeamSize, timeout.Token).ConfigureAwait(false);
                    if (IsSupersededByFinal(snapshot.UtteranceId, snapshot.IsFinal, _finalTranslationRevision))
                        continue;
                    if (string.IsNullOrWhiteSpace(translated.TranslatedText))
                    {
                        if (snapshot.IsFinal) FinishUtterance(snapshot.UtteranceId);
                        continue;
                    }

                    if (snapshot.IsFinal)
                    {
                        var display = new LocalIncomingTranslation(snapshot.DisplaySource, translated.TranslatedText,
                            translated.Language);
                        TranslationReady?.Invoke(this, display);
                        FinishUtterance(snapshot.UtteranceId);
                        VoiceScreenLog.Info($"Realtime final subtitle latency={ElapsedMilliseconds(snapshot.StartTimestamp)}ms");
                    }
                    else
                    {
                        // 临时译文是一段段拼起来的：这一次只翻了新确认的短语，
                        // 要接在此前累计的译文后面才是当前完整的中文。
                        var accumulated = TranscriptWindow.Join(
                            _committedTranslations.GetValueOrDefault(snapshot.UtteranceId, string.Empty),
                            translated.TranslatedText);
                        _committedTranslations[snapshot.UtteranceId] = accumulated;
                        SetPreview(snapshot.UtteranceId, new LocalIncomingTranslation(snapshot.DisplaySource,
                            accumulated, translated.Language));
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

    /// <summary>
    /// LocalAgreement-2：相邻两次识别一致的前缀才算稳定。比较的是「当前窗口」内的文本，
    /// 每次提交后上一次的假设会被 <see cref="TranscriptWindow.RebasePreviousHypothesis"/>
    /// 重新对齐到裁剪后的新窗口。
    /// </summary>
    private string SelectStableSource(AudioSnapshot snapshot, string windowText, string language)
    {
        if (!_previousHypotheses.TryGetValue(snapshot.UtteranceId, out var previous))
        {
            _previousHypotheses[snapshot.UtteranceId] = windowText;
            return IncrementalTranscript.EndsClause(windowText) ? windowText : string.Empty;
        }

        _previousHypotheses[snapshot.UtteranceId] = windowText;
        var stable = IncrementalTranscript.LongestStablePrefix(previous, windowText, language);
        if (stable.Length < IncrementalTranscript.MinimumStableLength(language)) return string.Empty;
        if (_lastStableSources.TryGetValue(snapshot.UtteranceId, out var last) && stable == last)
            return string.Empty;
        _lastStableSources[snapshot.UtteranceId] = stable;
        return stable;
    }

    /// <summary>
    /// 流式模式下从累计稳定前缀里取出还没翻译过的那一段。
    /// 识别结果回退（新前缀不再以旧前缀开头）时整段重来，宁可重复一次也不要漏字。
    /// </summary>
    private string NewlyStableChunk(long utteranceId, string stable)
    {
        var already = _committedSources.GetValueOrDefault(utteranceId, string.Empty);
        _committedSources[utteranceId] = stable;
        return already.Length > 0 && stable.StartsWith(already, StringComparison.OrdinalIgnoreCase)
            ? stable[already.Length..].Trim()
            : stable;
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
        _committedTranslations.TryRemove(utteranceId, out _);
        _committedSources.TryRemove(utteranceId, out _);
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
    /// <paramref name="CommittedText"/> 是这段音频出发时已确认的前缀，识别结果要接在它后面。
    /// </summary>
    private sealed record AudioSnapshot(long UtteranceId, int Revision, byte[] Buffer, int Length, bool IsFinal,
        long StartTimestamp, string CommittedText, bool IsFirstSnapshot);

    private sealed record TranslationSnapshot(long UtteranceId, int Revision, string DisplaySource,
        string SourceForTranslation, string Language, bool IsFinal, long StartTimestamp, int BeamSize);
}
