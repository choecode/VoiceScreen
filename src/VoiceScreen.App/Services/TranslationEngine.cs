using VoiceScreen.App.Audio;
using VoiceScreen.App.Diagnostics;
using VoiceScreen.App.Models;
using VoiceScreen.Core;

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
    private LocalIncomingAudioProcessor? _localIncoming;
    private Task? _demoTask;
    private int _processingOutgoing;

    public TranslationEngine(AppSettings settings)
    {
        _settings = settings;
        _state.StateChanged += (_, state) => StatusChanged?.Invoke(this, DescribeState(state));
    }

    public event EventHandler<(string Kind, string Text)>? SubtitleProduced;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? Error;
    public bool IsRunning { get; private set; }
    public bool PassThroughEnabled => _router.IsPassThroughEnabled;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        NormalizeAudioRoutingDevices();
        ValidateSettings();
        _router.Start(_settings.MicrophoneDeviceId, _settings.CableRenderDeviceId);
        if (_settings.DemoMode)
        {
            _demoTask = Task.Run(() => DemoIncomingLoopAsync(_lifetime.Token));
        }
        else
        {
            StatusChanged?.Invoke(this, "正在加载本地双向语音识别和翻译模型……");
            _localOutgoing = new LocalOutgoingService();
            await _localOutgoing.StartAsync(cancellationToken).ConfigureAwait(false);
            _localIncoming = new LocalIncomingAudioProcessor(_localOutgoing);
            _localIncoming.TranslationReady += OnIncomingTranslation;
            _localIncoming.Error += (_, message) => Error?.Invoke(this, message);
            _discordCapture = CreateIncomingCapture();
        }
        IsRunning = true;
        var mode = _settings.DemoMode ? "Demo" : "Local-Whisper-Qwen";
        var incomingLang = _settings.DemoMode ? "n/a" : "en->cn";
        VoiceScreenLog.Info($"TranslationEngine started. mode={mode} incomingLang={incomingLang}");
        StatusChanged?.Invoke(this, _settings.DemoMode
            ? "模拟模式运行中，原声麦克风已直通"
            : "纯本地模式 · 只监听 Discord · 原声麦克风已直通");
    }

    public void BeginLocalCapture()
    {
        if (!IsRunning || !_state.TryBeginLocalCapture()) return;
        try
        {
            _router.BeginTranslationCapture();
            SubtitleProduced?.Invoke(this, ("status", "正在听你说中文……"));
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
        try
        {
            if (!_state.TryBeginTranslation()) return;
            var captured = _router.EndTranslationCapture();
            if (captured.Duration < TimeSpan.FromMilliseconds(250) || captured.Data.Length == 0)
            {
                StatusChanged?.Invoke(this, "按键时间太短，已取消并恢复原声");
                return;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var token = timeout.Token;
            LocalOutgoingTranslation translation;
            if (_settings.DemoMode)
            {
                await Task.Delay(300, token).ConfigureAwait(false);
                translation = new LocalOutgoingTranslation("模拟模式：他们在二楼", "They're on the second floor.");
            }
            else
            {
                var pcm = AudioTranscoder.ToPcm16Mono16Khz(captured);
                var local = _localOutgoing ?? throw new InvalidOperationException("本地发送服务尚未启动。");
                var result = await local.TranslateSpeechAsync(pcm, token).ConfigureAwait(false);
                translation = result;
            }

            if (string.IsNullOrWhiteSpace(translation.TranslatedText))
                throw new InvalidOperationException("没有得到英文翻译结果。");

            SubtitleProduced?.Invoke(this, ("mine", $"我说：{translation.SourceText}"));
            SubtitleProduced?.Invoke(this, ("sent", $"已发送：{translation.TranslatedText}"));
            _echo.RememberSent(translation.TranslatedText);
            if (!_state.TryBeginTts()) throw new InvalidOperationException("发送状态异常。");

            var tts = await OfflineSpeech.SynthesizeEnglishAsync(translation.TranslatedText, token).ConfigureAwait(false);
            await _router.PlayTtsAsync(tts, token).ConfigureAwait(false);
            _state.TryBeginCooldown();
            await Task.Delay(500, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Error?.Invoke(this, "本次翻译超过 10 秒，已取消并恢复原声麦克风。");
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

    public async Task PlayTestPhraseAsync(CancellationToken cancellationToken)
    {
        if (!IsRunning) throw new InvalidOperationException("请先启动程序。");
        if (!_state.TryBeginLocalCapture()) throw new InvalidOperationException("当前正在处理另一条语音。");
        _router.BeginTranslationCapture();
        try
        {
            _state.TryBeginTranslation();
            const string text = "VoiceScreen audio routing test. The virtual microphone is working.";
            SubtitleProduced?.Invoke(this, ("sent", $"测试发送：{text}"));
            _state.TryBeginTts();
            var audio = await OfflineSpeech.SynthesizeEnglishAsync(text, cancellationToken).ConfigureAwait(false);
            await _router.PlayTtsAsync(audio, cancellationToken).ConfigureAwait(false);
            _state.TryBeginCooldown();
        }
        finally
        {
            _router.RestorePassThrough();
            _state.Complete();
        }
    }

    private ValueTask SendIncomingFrameAsync(byte[] frame, CancellationToken cancellationToken)
    {
        if (_localIncoming is null) return ValueTask.CompletedTask;
        return _localIncoming.AddFrameAsync(frame, _state.ShouldAcceptRemoteResult, cancellationToken);
    }

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
        var subtitle = string.IsNullOrWhiteSpace(result.SourceText)
            ? $"中文：{result.TranslatedText}"
            : $"EN：{result.SourceText}\n中：{result.TranslatedText}";
        // 英文原文和中文译文作为同一个字幕项，滚动和淘汰时不会错位。
        SubtitleProduced?.Invoke(this, ("remote", subtitle));
    }

    private async Task DemoIncomingLoopAsync(CancellationToken cancellationToken)
    {
        var examples = new[]
        {
            "EN：This is a simulated subtitle.\n中：这是模拟字幕。",
            "EN：Can you hear me?\n中：你能听见我吗？",
            "EN：Let's move to the left.\n中：我们往左边走。"
        };
        var index = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
            if (_state.ShouldAcceptRemoteResult)
                SubtitleProduced?.Invoke(this, ("remote", examples[index++ % examples.Length]));
        }
    }

    private void ValidateSettings()
    {
        if (string.IsNullOrWhiteSpace(_settings.MicrophoneDeviceId)) throw new InvalidOperationException("请选择实体麦克风。");
        if (string.IsNullOrWhiteSpace(_settings.CableRenderDeviceId)) throw new InvalidOperationException("请选择 CABLE Input 虚拟播放设备。");
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
    }

    private static string DescribeState(DuplexState state) => state switch
    {
        DuplexState.CapturingLocalChinese => "正在听中文（原声已暂停）",
        DuplexState.TranslatingLocalText => "正在翻译中文",
        DuplexState.SendingEnglishTts => "正在向 Discord 发送英文",
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
        if (_localIncoming is not null)
        {
            _localIncoming.TranslationReady -= OnIncomingTranslation;
            try { await _localIncoming.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { VoiceScreenLog.Warn($"local incoming processor dispose error: {ex.Message}"); }
            _localIncoming = null;
        }
        if (_localOutgoing is not null)
        {
            try { await _localOutgoing.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { VoiceScreenLog.Warn($"local outgoing service dispose error: {ex.Message}"); }
            _localOutgoing = null;
        }
        if (_demoTask is not null)
        {
            try { await _demoTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _lifetime.Dispose();
        VoiceScreenLog.Info("TranslationEngine disposed");
    }
}
