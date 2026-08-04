using VoiceScreen.App.Audio;
using VoiceScreen.App.Diagnostics;
using VoiceScreen.App.Models;
using VoiceScreen.Core;
using System.Diagnostics;

namespace VoiceScreen.App.Services;

public sealed class TranslationEngine : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly MicrophoneCableRouter _router = new();
    private readonly DuplexStateMachine _state = new();
    private readonly EchoSuppressor _echo = new();
    private readonly CancellationTokenSource _lifetime = new();
    private IDisposable? _discordCapture;
    private LocalOutgoingService? _localOutgoing;
    private OnlineApiService? _remote;
    private IIncomingAudioProcessor? _incoming;
    private int _processingOutgoing;

    public TranslationEngine(AppSettings settings)
    {
        _settings = settings;
        _state.StateChanged += (_, state) => StatusChanged?.Invoke(this, DescribeState(state));
    }

    public event EventHandler<(string Kind, string Text)>? SubtitleProduced;
    public event EventHandler<string?>? SubtitlePreviewChanged;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? Error;
    public bool IsRunning { get; private set; }
    public bool PassThroughEnabled => _router.IsPassThroughEnabled;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        NormalizeAudioRoutingDevices();
        ValidateSettings();
        _router.Start(_settings.MicrophoneDeviceId, _settings.CableRenderDeviceId, _settings.MonitorRenderDeviceId);
        StatusChanged?.Invoke(this, "正在加载本机 Whisper 与本地翻译备用模型……");
        _localOutgoing = new LocalOutgoingService();
        await _localOutgoing.StartAsync(cancellationToken).ConfigureAwait(false);
        _remote = new OnlineApiService(_settings.ApiEnglishVoice);
        if (_settings.UseApiTranslation || _settings.UseApiTts)
            await _remote.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        _incoming = new LocalIncomingAudioProcessor(_localOutgoing, _settings.LowLatencyIncoming,
            TranslateIncomingSelectedAsync);
        SubscribeIncoming(_incoming);
        await _incoming.StartAsync(cancellationToken).ConfigureAwait(false);
        _discordCapture = CreateIncomingCapture();
        IsRunning = true;
        VoiceScreenLog.Info($"TranslationEngine started. ASR=local translation={ProviderName(_settings.UseApiTranslation)} tts={ProviderName(_settings.UseApiTts)} lowLatency={_settings.LowLatencyIncoming}");
        StatusChanged?.Invoke(this, $"运行中 · ASR=本地 · 翻译={ProviderName(_settings.UseApiTranslation)} · TTS={ProviderName(_settings.UseApiTts)} · 只监听 Discord");
    }

    public void UpdateProviders(bool useApiTranslation, bool useApiTts)
    {
        _settings.UseApiTranslation = useApiTranslation;
        _settings.UseApiTts = useApiTts;
        VoiceScreenLog.Info($"Providers switched. translation={ProviderName(useApiTranslation)} tts={ProviderName(useApiTts)}");
    }

    public void BeginLocalCapture()
    {
        if (!IsRunning || !_state.TryBeginLocalCapture()) return;
        try
        {
            _router.BeginTranslationCapture();
        }
        catch (Exception ex)
        {
            _router.RestorePassThrough();
            _state.Fault();
            Error?.Invoke(this, ex.Message);
            _state.Reset();
        }
    }

    public async Task EndLocalCaptureAsync()
    {
        if (!IsRunning || Interlocked.Exchange(ref _processingOutgoing, 1) != 0) return;
        var stage = "准备";
        try
        {
            if (!_state.TryBeginTranslation()) return;
            var captured = _router.EndTranslationCapture();
            if (captured.Duration < TimeSpan.FromMilliseconds(250) || captured.Data.Length == 0)
            {
                StatusChanged?.Invoke(this, "按键时间太短，已取消并恢复原声");
                return;
            }

            // Freeze the routing choice for this utterance. UI changes apply to the next one.
            var useApiTranslation = _settings.UseApiTranslation;
            var useApiTts = _settings.UseApiTts;

            LocalOutgoingTranslation translation;
            stage = "识别和翻译";
            var translationTimer = Stopwatch.StartNew();
            using var translationTimeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            translationTimeout.CancelAfter(TimeSpan.FromSeconds(35));
            var translationToken = translationTimeout.Token;
            var pcm = AudioTranscoder.ToPcm16Mono16Khz(captured);
            var local = _localOutgoing ?? throw new InvalidOperationException("本地识别服务尚未启动。");
            var transcription = await local.TranscribeChineseSpeechAsync(pcm, translationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(transcription.Text))
                throw new InvalidOperationException("本机中文识别没有听清，请再说一次。");
            var english = useApiTranslation
                ? await (_remote ?? throw new InvalidOperationException("在线翻译尚未就绪。"))
                    .TranslateChineseAsync(transcription.Text, translationToken).ConfigureAwait(false)
                : await local.TranslateChineseTextAsync(transcription.Text, translationToken).ConfigureAwait(false);
            translation = new LocalOutgoingTranslation(transcription.Text, english);
            translationTimer.Stop();
            VoiceScreenLog.Info($"Outgoing ASR+translation completed in {translationTimer.ElapsedMilliseconds}ms");

            if (string.IsNullOrWhiteSpace(translation.TranslatedText))
                throw new InvalidOperationException("没有得到英文翻译结果。");

            SubtitleProduced?.Invoke(this, ("mine", $"我说：{translation.SourceText}"));
            SubtitleProduced?.Invoke(this, ("sent", $"已发送：{translation.TranslatedText}"));
            _echo.RememberSent(translation.TranslatedText);
            if (!_state.TryBeginTts()) throw new InvalidOperationException("发送状态异常。");

            stage = "英文语音合成和播放";
            var playbackTimer = Stopwatch.StartNew();
            using var playbackTimeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            playbackTimeout.CancelAfter(TimeSpan.FromSeconds(60));
            var playbackToken = playbackTimeout.Token;
            var tts = useApiTts
                ? await (_remote ?? throw new InvalidOperationException("Edge TTS 尚未就绪。"))
                    .SynthesizeEnglishAsync(translation.TranslatedText, playbackToken).ConfigureAwait(false)
                : await OfflineSpeech.SynthesizeEnglishAsync(translation.TranslatedText, playbackToken,
                    _settings.EnglishVoiceName).ConfigureAwait(false);
            await _router.PlayTtsAsync(tts, playbackToken, _settings.MonitorTranslatedSpeech).ConfigureAwait(false);
            _state.TryBeginCooldown();
            await Task.Delay(500, playbackToken).ConfigureAwait(false);
            playbackTimer.Stop();
            VoiceScreenLog.Info($"Outgoing TTS+playback completed in {playbackTimer.ElapsedMilliseconds}ms");
        }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
        {
            var limit = stage == "识别和翻译" ? "35 秒" : "60 秒";
            Error?.Invoke(this, $"本次{stage}超过 {limit}，已取消并恢复原声麦克风。");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // 程序正常停止时不向字幕历史写入超时错误。
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex.Message);
        }
        finally
        {
            _router.RestorePassThrough();
            _state.Complete();
            Interlocked.Exchange(ref _processingOutgoing, 0);
            StatusChanged?.Invoke(this, "原声麦克风已恢复");
        }
    }

    public async Task TranslateAndPreviewAsync(string chineseText, CancellationToken cancellationToken)
    {
        if (!IsRunning) throw new InvalidOperationException("请先启动程序。");
        if (string.IsNullOrWhiteSpace(chineseText)) throw new InvalidOperationException("请输入要测试的中文。");
        if (Interlocked.Exchange(ref _processingOutgoing, 1) != 0)
            throw new InvalidOperationException("当前正在处理另一条语音。");
        try
        {
            var useApiTranslation = _settings.UseApiTranslation;
            var useApiTts = _settings.UseApiTts;
            if (!_state.TryBeginLocalCapture()) throw new InvalidOperationException("当前正在处理另一条语音。");
            _router.BeginTranslationCapture();
            _state.TryBeginTranslation();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var english = useApiTranslation
                ? await (_remote ?? throw new InvalidOperationException("在线翻译尚未就绪。"))
                    .TranslateChineseAsync(chineseText, timeout.Token).ConfigureAwait(false)
                : await (_localOutgoing ?? throw new InvalidOperationException("本地翻译服务尚未启动。"))
                    .TranslateChineseTextAsync(chineseText, timeout.Token).ConfigureAwait(false);
            SubtitleProduced?.Invoke(this, ("mine", $"测试中文：{chineseText.Trim()}"));
            SubtitleProduced?.Invoke(this, ("sent", $"试听英文：{english}"));
            _state.TryBeginTts();
            var audio = useApiTts
                ? await (_remote ?? throw new InvalidOperationException("Edge TTS 尚未就绪。"))
                    .SynthesizeEnglishAsync(english, timeout.Token).ConfigureAwait(false)
                : await OfflineSpeech.SynthesizeEnglishAsync(english, timeout.Token,
                    _settings.EnglishVoiceName).ConfigureAwait(false);
            await _router.PlayMonitorTtsAsync(audio, timeout.Token).ConfigureAwait(false);
            _state.TryBeginCooldown();
        }
        finally
        {
            _router.RestorePassThrough();
            _state.Complete();
            Interlocked.Exchange(ref _processingOutgoing, 0);
            StatusChanged?.Invoke(this, "试听完成，原声麦克风已恢复");
        }
    }

    private ValueTask SendIncomingFrameAsync(byte[] frame, CancellationToken cancellationToken)
    {
        if (_incoming is null) return ValueTask.CompletedTask;
        return _incoming.AddFrameAsync(frame, _state.ShouldAcceptRemoteResult, cancellationToken);
    }

    private void SubscribeIncoming(IIncomingAudioProcessor incoming)
    {
        incoming.TranslationReady += OnIncomingTranslation;
        incoming.PreviewChanged += OnIncomingPreview;
        incoming.Error += OnIncomingError;
        incoming.Status += OnIncomingStatus;
    }

    private void OnIncomingError(object? sender, string message) => Error?.Invoke(this, message);

    /// <summary>只捕获 Discord 进程树；失败时不允许回退到整张声卡，避免游戏声音进入字幕。</summary>
    private IDisposable CreateIncomingCapture()
    {
        var pid = DiscordProcessLocator.FindMainProcessId()
            ?? throw new InvalidOperationException("没有找到 Discord 桌面客户端。请先启动 Discord，再启动 VoiceScreen。");
        VoiceScreenLog.Info($"Discord root process located. pid={pid}");

        var capture = new DiscordProcessLoopbackCapture();
        capture.FrameReady += SendIncomingFrameAsync;
        try
        {
            capture.Start(pid);
            return capture;
        }
        catch
        {
            capture.FrameReady -= SendIncomingFrameAsync;
            capture.Dispose();
            throw;
        }
    }

    private void OnIncomingTranslation(object? sender, LocalIncomingTranslation result)
    {
        if (!_state.ShouldAcceptRemoteResult || string.IsNullOrWhiteSpace(result.TranslatedText)) return;
        if (_echo.IsLikelyEcho(result.SourceText)) return;
        if (!result.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            && !result.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            && !result.Language.StartsWith("th", StringComparison.OrdinalIgnoreCase)) return;
        var subtitle = result.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? $"中：{result.SourceText}"
            : result.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                ? $"EN：{result.SourceText}\n中：{result.TranslatedText}"
                : $"TH：{result.SourceText}\n中：{result.TranslatedText}";
        // 英文原文和中文译文作为同一个字幕项，滚动和淘汰时不会错位。
        SubtitleProduced?.Invoke(this, ("remote", subtitle));
    }

    private void OnIncomingPreview(object? sender, LocalIncomingTranslation? result)
    {
        if (result is null || !_state.ShouldAcceptRemoteResult)
        {
            SubtitlePreviewChanged?.Invoke(this, null);
            return;
        }
        if (string.IsNullOrWhiteSpace(result.SourceText) || _echo.IsLikelyEcho(result.SourceText)) return;
        if (!IsSupportedIncomingLanguage(result.Language)) return;
        var label = result.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "中"
            : result.Language.StartsWith("th", StringComparison.OrdinalIgnoreCase) ? "TH" : "EN";
        var preview = $"{label}（实时）：{result.SourceText}";
        if (!result.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(result.TranslatedText))
            preview += $"\n中（实时）：{result.TranslatedText}";
        SubtitlePreviewChanged?.Invoke(this, preview);
    }

    private static bool IsSupportedIncomingLanguage(string language)
        => language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
           || language.StartsWith("en", StringComparison.OrdinalIgnoreCase)
           || language.StartsWith("th", StringComparison.OrdinalIgnoreCase);

    private void OnIncomingStatus(object? sender, string message) => StatusChanged?.Invoke(this, message);

    private void ValidateSettings()
    {
        if (string.IsNullOrWhiteSpace(_settings.MicrophoneDeviceId)) throw new InvalidOperationException("请选择实体麦克风。");
        if (string.IsNullOrWhiteSpace(_settings.CableRenderDeviceId)) throw new InvalidOperationException("请选择 CABLE Input 虚拟播放设备。");
        if (string.IsNullOrWhiteSpace(_settings.MonitorRenderDeviceId))
            throw new InvalidOperationException("请选择英文试听耳机。");
    }

    /// <summary>
    /// 迁移旧版本保存的错误路由。旧界面可能把 HyperX 耳机 ID 存进 CableRenderDeviceId，
    /// 启动前按设备名称强制纠正，确保麦克风永远不会被回放到实体耳机。
    /// </summary>
    private void NormalizeAudioRoutingDevices()
    {
        var deviceService = new AudioDeviceService();
        var renderDevices = deviceService.GetRenderDevices();
        var configuredCable = renderDevices.FirstOrDefault(device => device.Id == _settings.CableRenderDeviceId);
        if (configuredCable is null || !AudioDeviceService.IsVirtualCableInput(configuredCable))
        {
            var cable = AudioDeviceService.FindVirtualCableInput(renderDevices)
                ?? throw new InvalidOperationException(
                    "没有检测到 CABLE Input (VB-Audio Virtual Cable)。请安装或启用 VB-CABLE 后重试。");
            _settings.CableRenderDeviceId = cable.Id;
            VoiceScreenLog.Info($"Audio routing corrected automatically: virtual output={cable.Name}");
        }
        var configuredMonitor = renderDevices.FirstOrDefault(device => device.Id == _settings.MonitorRenderDeviceId);
        if (configuredMonitor is null || AudioDeviceService.IsVirtualCableInput(configuredMonitor))
        {
            var monitor = AudioDeviceService.FindBest(renderDevices.Where(device => !AudioDeviceService.IsVirtualCableInput(device)),
                string.Empty, "HyperX", "耳机", "Headphones")
                ?? throw new InvalidOperationException("没有找到可用于英文试听的实体耳机。");
            _settings.MonitorRenderDeviceId = monitor.Id;
            VoiceScreenLog.Info($"Audio monitor selected automatically: output={monitor.Name}");
        }
        var voices = OfflineSpeech.GetInstalledEnglishVoices();
        if (voices.Count == 0)
            throw new InvalidOperationException("没有检测到 Windows 英文语音，请在 Windows 语音设置中安装英文男声或女声。");
        if (!voices.Any(voice => voice.Id == _settings.EnglishVoiceName))
        {
            _settings.EnglishVoiceName = voices[0].Id;
            VoiceScreenLog.Info($"English TTS voice selected automatically: voice={voices[0].Name}");
        }
    }

    private Task<LocalIncomingTranslation> TranslateIncomingSelectedAsync(string text, string language,
        CancellationToken cancellationToken)
        => _settings.UseApiTranslation
            ? (_remote ?? throw new InvalidOperationException("在线翻译尚未就绪。"))
                .TranslateIncomingAsync(text, language, cancellationToken)
            : (_localOutgoing ?? throw new InvalidOperationException("本地翻译尚未就绪。"))
                .TranslateIncomingTextAsync(text, language, cancellationToken);

    private static string ProviderName(bool online) => online ? "API" : "本地";

    private static string DescribeState(DuplexState state) => state switch
    {
        DuplexState.CapturingLocalChinese => "正在听你说中文……（原声已暂停）",
        DuplexState.TranslatingLocalText => "正在翻译中文",
        DuplexState.SendingEnglishTts => "正在播放英文（接收识别与原声麦克风已暂停）",
        DuplexState.Cooldown => "发送完成，正在恢复原声",
        DuplexState.Faulted => "发生错误",
        _ => "原声麦克风直通中"
    };

    public async ValueTask DisposeAsync()
    {
        IsRunning = false;
        VoiceScreenLog.Info("TranslationEngine dispose: cancelling lifetime token");
        _lifetime.Cancel();
        _router.RestorePassThrough();
        _router.Dispose();
        if (_discordCapture is not null)
        {
            try
            {
                if (_discordCapture is IAsyncDisposable asyncDisposable)
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                else
                    _discordCapture.Dispose();
            }
            catch (Exception ex)
            {
                VoiceScreenLog.Warn($"incoming capture dispose error: {ex.Message}");
            }
        }
        if (_incoming is not null)
        {
            _incoming.TranslationReady -= OnIncomingTranslation;
            _incoming.PreviewChanged -= OnIncomingPreview;
            _incoming.Error -= OnIncomingError;
            _incoming.Status -= OnIncomingStatus;
            try { await _incoming.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { VoiceScreenLog.Warn($"incoming processor dispose error: {ex.Message}"); }
            _incoming = null;
        }
        if (_localOutgoing is not null)
        {
            try { await _localOutgoing.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { VoiceScreenLog.Warn($"local outgoing service dispose error: {ex.Message}"); }
            _localOutgoing = null;
        }
        if (_remote is not null)
        {
            _remote.Dispose();
            _remote = null;
        }
        _lifetime.Dispose();
        VoiceScreenLog.Info("TranslationEngine disposed");
    }
}
