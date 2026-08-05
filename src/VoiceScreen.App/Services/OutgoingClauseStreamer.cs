using System.Diagnostics;
using VoiceScreen.App.Audio;
using VoiceScreen.App.Diagnostics;
using VoiceScreen.Core;

namespace VoiceScreen.App.Services;

/// <summary>
/// 发送方向的分句抢跑。
///
/// 一期是「松开右 Alt 才开始识别整句」，对方听到第一个英文单词的时间等于
/// 说话时长 + 识别 + 翻译 + 整句合成。抢跑把它改成流水线：按住键的过程中就持续识别，
/// 每当一个短句在标点处说完，立刻翻译、合成、送进 VB-CABLE，后面的中文继续识别。
/// 对方通常能提早一到三秒听到第一句。
///
/// 三条不可动摇的约束：
/// 1. 已经播出去的英文收不回来，所以只有 LocalAgreement 判定稳定、并且落在标点上的
///    完整短句才允许进队列（见 <see cref="ClauseSegmenter"/>）。
/// 2. 播报期间麦克风直通已经是静音的（<c>BeginTranslationCapture</c> 把音量压到 0），
///    接收方向也已被 <see cref="DuplexStateMachine.ShouldAcceptRemoteResult"/> 挡住，
///    所以不会出现程序听见自己说话的回环。
/// 3. 松手时剩下的尾巴必须由调用方补发，否则用户说的最后半句会被静默吞掉——
///    <see cref="SpokenPrefix"/> 就是给调用方做这件事的依据。
/// </summary>
public sealed class OutgoingClauseStreamer : IAsyncDisposable
{
    /// <summary>轮询间隔。比接收方向的 600ms 略长：中文短句的标点密度低于英文。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(700);

    /// <summary>录音短于这个长度不值得送去识别，只会得到噪声。</summary>
    private static readonly TimeSpan MinimumAudioForRecognition = TimeSpan.FromMilliseconds(900);

    private readonly Func<CapturedAudio?> _peekAudio;
    private readonly Func<string, CancellationToken, Task<string>> _translate;
    private readonly Func<string, CancellationToken, Task<byte[]>> _synthesize;
    private readonly Action<byte[]> _enqueueAudio;
    private readonly LocalOutgoingService _localService;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<string> _spokenClauses = [];
    private readonly object _spokenGate = new();
    private Task? _worker;
    private string _previousHypothesis = string.Empty;
    private int _consumedCharacters;

    public OutgoingClauseStreamer(LocalOutgoingService localService, Func<CapturedAudio?> peekAudio,
        Func<string, CancellationToken, Task<string>> translate,
        Func<string, CancellationToken, Task<byte[]>> synthesize, Action<byte[]> enqueueAudio)
    {
        _localService = localService;
        _peekAudio = peekAudio;
        _translate = translate;
        _synthesize = synthesize;
        _enqueueAudio = enqueueAudio;
    }

    /// <summary>已经抢先播出去的中文短句，按顺序拼接。调用方用它组装字幕。</summary>
    public string SpokenPrefix
    {
        get { lock (_spokenGate) return string.Concat(_spokenClauses); }
    }

    /// <summary>已经抢先播出去的中文字符数，收尾时用来切掉重复的部分。</summary>
    public int SpokenCharacters
    {
        get { lock (_spokenGate) return _spokenClauses.Sum(clause => clause.Length); }
    }

    public bool HasSpoken => SpokenCharacters > 0;

    public event EventHandler<(string Chinese, string English)>? ClauseSpoken;

    public void Start()
    {
        _worker ??= Task.Run(RunAsync);
    }

    /// <summary>停止抢跑并等待正在进行的那一段播完入队，避免和收尾的补发交错。</summary>
    public async Task StopAsync()
    {
        if (_worker is null) return;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try { await _worker.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _worker = null;
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, _lifetime.Token).ConfigureAwait(false);
                await PollOnceAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 松手或程序停止，正常路径。
        }
    }

    private async Task PollOnceAsync()
    {
        var captured = _peekAudio();
        if (captured is null || captured.Duration < MinimumAudioForRecognition) return;

        try
        {
            var stable = await RecognizeStablePrefixAsync(captured).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(stable)) return;

            var split = ClauseSegmenter.Split(stable, _consumedCharacters);
            if (split.Clauses.Count == 0) return;
            _consumedCharacters = split.ConsumedCharacters;

            foreach (var clause in split.Clauses)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                await SpeakClauseAsync(clause).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 抢跑是纯增益：任何一次失败都退回「松手后整句处理」这条一期路径，
            // 绝不能让它把正常发送也带崩。
            VoiceScreenLog.Warn($"Outgoing clause streaming skipped one round: {ex.Message}");
        }
    }

    private async Task<string> RecognizeStablePrefixAsync(CapturedAudio captured)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var pcm = AudioTranscoder.ToPcm16Mono16Khz(captured);
        var transcription = await _localService
            .TranscribeChineseSpeechAsync(pcm, new TranscriptionRequest { Preview = true }, timeout.Token)
            .ConfigureAwait(false);
        var text = transcription.Text;
        if (string.IsNullOrWhiteSpace(text) || TranscriptSanitizer.IsPathologicalRepetition(text))
            return string.Empty;

        // 和接收方向同一套 LocalAgreement-2：只有连续两次识别一致的前缀才算稳定。
        // 抢跑比接收方向更不能出错——字幕可以改，已经播出去的语音不能。
        var previous = _previousHypothesis;
        _previousHypothesis = text;
        return IncrementalTranscript.LongestStablePrefix(previous, text, SpokenLanguage.Chinese);
    }

    private async Task SpeakClauseAsync(string clause)
    {
        var timer = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var english = await _translate(clause, timeout.Token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(english)) return;
        var audio = await _synthesize(english, timeout.Token).ConfigureAwait(false);
        if (audio.Length == 0) return;

        // 入队即返回：这一段开始播的同时，下一段已经可以进入识别和翻译。
        _enqueueAudio(audio);
        lock (_spokenGate) _spokenClauses.Add(clause);
        ClauseSpoken?.Invoke(this, (clause, english));
        VoiceScreenLog.Info($"Outgoing clause streamed in {timer.ElapsedMilliseconds}ms: {english}");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
