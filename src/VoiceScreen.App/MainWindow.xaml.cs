using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using VoiceScreen.App.Audio;
using VoiceScreen.App.Diagnostics;
using VoiceScreen.App.Input;
using VoiceScreen.App.Models;
using VoiceScreen.App.Services;

namespace VoiceScreen.App;

public partial class MainWindow : Window
{
    private readonly AudioDeviceService _devices = new();
    private readonly SettingsStore _settingsStore = new();
    private readonly RightAltHoldHook _hook = new();
    private AppSettings _settings;
    private TranslationEngine? _engine;
    private OverlayWindow? _overlay;
    private IntPtr _windowHandle;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _settingsStore.Load();
        Loaded += OnLoaded;
        Closing += OnClosing;
        SourceInitialized += OnSourceInitialized;
        _hook.Pressed += (_, _) => _engine?.BeginLocalCapture();
        _hook.PageUpPressed += (_, _) => _overlay?.ScrollPage(down: false);
        _hook.PageDownPressed += (_, _) => _overlay?.ScrollPage(down: true);
        _hook.Released += async (_, _) =>
        {
            if (_engine is not null) await _engine.EndLocalCaptureAsync();
        };
    }

    /// <summary>
    /// WPF 在 <c>HwndSource.Initialize</c> 时会偷偷给窗口调 <c>RegisterDragDrop</c>。
    /// 在某些中文 Windows 11 + 第三方 shell extension 环境下，<c>InterfaceMarshaler.ConvertToNative</c>
    /// 会触发 <c>STATUS_STACK_BUFFER_OVERRUN (0xc0000409)</c> fastfail，整个进程被杀。
    /// 在窗口 handle 已经就绪后立刻 <c>RevokeDragDrop</c>，WPF 的拖拽事件就不再注册，
    /// 不影响我们的业务功能（VoiceScreen 本来就不需要接受拖拽文件）。
    /// </summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            _windowHandle = hwnd;
            if (hwnd != IntPtr.Zero)
            {
                RevokeDragDrop(hwnd);
                VoiceScreenLog.Info("RevokeDragDrop called to avoid WPF BEX64 on OLE registration");
            }
        }
        catch (Exception ex)
        {
            VoiceScreenLog.Warn($"RevokeDragDrop failed: {ex.Message}");
        }
    }

    [DllImport("ole32.dll")]
    private static extern int RevokeDragDrop(IntPtr hwnd);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Migrate the old all-or-nothing cloud switch once; new releases persist both choices separately.
        if (_settings.CloudMode && !_settings.UseApiTranslation && !_settings.UseApiTts)
        {
            _settings.UseApiTranslation = true;
            _settings.UseApiTts = true;
        }
        AsrProviderCombo.SelectedIndex = string.Equals(_settings.AsrEngine, "sherpa", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        TranslationProviderCombo.SelectedIndex = _settings.UseApiTranslation ? 1 : 0;
        TtsProviderCombo.SelectedIndex = _settings.UseApiTts ? 1 : 0;
        LowLatencyIncomingCheckBox.IsChecked = _settings.LowLatencyIncoming;
        MonitorTranslatedSpeechCheckBox.IsChecked = _settings.MonitorTranslatedSpeech;
        SubtitleFontSizeSlider.Value = Math.Clamp(_settings.SubtitleFontSize, 14, 42);
        UseProcessLoopbackCheckBox.IsChecked = true;
        var savedApiVoice = string.IsNullOrWhiteSpace(_settings.ApiEnglishVoice)
            ? "en-US-JennyNeural"
            : _settings.ApiEnglishVoice;
        ApiEnglishVoiceCombo.ItemsSource = new[]
        {
            new ApiVoiceOption("en-US-JennyNeural", "Jenny · 美式英文女声"),
            new ApiVoiceOption("en-US-GuyNeural", "Guy · 美式英文男声")
        };
        ApiEnglishVoiceCombo.SelectedItem = ApiEnglishVoiceCombo.Items.Cast<ApiVoiceOption>()
            .FirstOrDefault(voice => voice.Id == savedApiVoice) ?? ApiEnglishVoiceCombo.Items[0];
        var elevated = IsElevated();
        RestartElevatedButton.IsEnabled = !elevated;
        RestartElevatedButton.Content = elevated ? "已是管理员模式" : "以管理员重启（战地）";
        VoiceScreenLog.Info($"Process integrity: elevated={elevated}");
        RefreshDevices();
    }

    /// <summary>
    /// 给命令行 --auto-start 用的代码路径：等同点击"启动"按钮。
    /// </summary>
    public void AutoStart() => StartButton_Click(this, new RoutedEventArgs());

    /// <summary>
    /// 给命令行 --auto-start 配套的：等同点击"停止"按钮。
    /// </summary>
    public void AutoStop() => StopButton_Click(this, new RoutedEventArgs());

    private void RefreshDevices()
    {
        var captures = _devices.GetCaptureDevices();
        var renders = _devices.GetRenderDevices();
        var voices = OfflineSpeech.GetInstalledEnglishVoices();
        var physicalMicrophones = captures
            .Where(device => !device.Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var automaticDiscordCapture = new[]
        {
            new AudioDeviceOption(string.Empty, "自动：仅捕获 Discord 进程（无需选择）")
        };
        MicrophoneCombo.ItemsSource = physicalMicrophones;
        var physicalOutputs = renders.Where(device => !AudioDeviceService.IsVirtualCableInput(device)).ToArray();
        MonitorOutputCombo.ItemsSource = physicalOutputs;
        EnglishVoiceCombo.ItemsSource = voices;
        DiscordOutputCombo.ItemsSource = automaticDiscordCapture;
        var cableInputs = renders.Where(AudioDeviceService.IsVirtualCableInput).ToArray();
        CableRenderCombo.ItemsSource = cableInputs;
        MicrophoneCombo.SelectedItem = AudioDeviceService.FindBest(physicalMicrophones, _settings.MicrophoneDeviceId, "HyperX", "麦克风", "Microphone");
        MonitorOutputCombo.SelectedItem = AudioDeviceService.FindBest(physicalOutputs, _settings.MonitorRenderDeviceId,
            "HyperX", "耳机", "Headphones");
        EnglishVoiceCombo.SelectedItem = voices.FirstOrDefault(voice => voice.Id == _settings.EnglishVoiceName)
            ?? voices.FirstOrDefault();
        DiscordOutputCombo.SelectedIndex = 0;
        CableRenderCombo.SelectedItem = AudioDeviceService.FindVirtualCableInput(renders);
        StatusText.Text = cableInputs.Length == 0
            ? "错误：没有检测到 CABLE Input (VB-Audio Virtual Cable)，请先安装或启用 VB-CABLE。"
            : voices.Count == 0
                ? "错误：没有检测到 Windows 英文语音，请先安装英文男声或女声。"
                : "设备已自动配置：HyperX 麦克风 → CABLE Input；接收只监听 Discord。";
    }

    private AppSettings ReadSettings()
    {
        return new AppSettings
        {
            DemoMode = false,
            CloudMode = false,
            AsrEngine = AsrProviderFromSelection(),
            UseApiTranslation = TranslationProviderCombo.SelectedIndex == 1,
            UseApiTts = TtsProviderCombo.SelectedIndex == 1,
            LowLatencyIncoming = LowLatencyIncomingCheckBox.IsChecked == true,
            UseProcessLoopback = true,
            MicrophoneDeviceId = (MicrophoneCombo.SelectedItem as AudioDeviceOption)?.Id ?? string.Empty,
            DiscordOutputDeviceId = (DiscordOutputCombo.SelectedItem as AudioDeviceOption)?.Id ?? string.Empty,
            CableRenderDeviceId = (CableRenderCombo.SelectedItem as AudioDeviceOption)?.Id ?? string.Empty,
            MonitorRenderDeviceId = (MonitorOutputCombo.SelectedItem as AudioDeviceOption)?.Id ?? string.Empty,
            MonitorTranslatedSpeech = MonitorTranslatedSpeechCheckBox.IsChecked == true,
            EnglishVoiceName = (EnglishVoiceCombo.SelectedItem as SpeechVoiceOption)?.Id ?? string.Empty,
            ApiEnglishVoice = (ApiEnglishVoiceCombo.SelectedItem as ApiVoiceOption)?.Id
                              ?? "en-US-JennyNeural",
            MaxSubtitleLines = _settings.MaxSubtitleLines,
            OverlayLeft = _settings.OverlayLeft,
            OverlayTop = _settings.OverlayTop,
            OverlayWidth = _settings.OverlayWidth,
            OverlayHeight = _settings.OverlayHeight,
            SubtitleFontSize = SubtitleFontSizeSlider.Value
        };
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings = ReadSettings();
            _settingsStore.Save(_settings);
            _overlay = new OverlayWindow(_settings.MaxSubtitleLines, _settings.OverlayLeft, _settings.OverlayTop,
                _settings.OverlayWidth, _settings.OverlayHeight, _settings.SubtitleFontSize);
            _overlay.Show();
            _engine = new TranslationEngine(_settings);
            _engine.SubtitleProduced += OnSubtitleProduced;
            _engine.SubtitlePreviewChanged += OnSubtitlePreviewChanged;
            _engine.StatusChanged += OnStatusChanged;
            _engine.Error += OnError;
            await _engine.StartAsync(CancellationToken.None);
            _hook.Start(_windowHandle);
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            TestTranslationButton.IsEnabled = !_settings.DemoMode;
            AdjustOverlayButton.IsEnabled = true;
            TestCloudApiButton.IsEnabled = false;
            _overlay.SetStatus("运行中 · 原声麦克风直通", ok: true);
        }
        catch (Exception ex)
        {
            // 启动失败：把完整堆栈写进日志，下次问问题时直接看日志就能定位。
            VoiceScreenLog.Error("StartButton_Click failed", ex);
            await StopEngineAsync();
            OnError(this, ex.Message);
            MessageBox.Show(this, $"{ex.Message}\n\n详细堆栈已写入 %LOCALAPPDATA%\\VoiceScreen\\voicescreen.log",
                "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e) => await StopEngineAsync();

    private async void TestTranslationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_engine is null) return;
        try
        {
            TestTranslationButton.IsEnabled = false;
            await _engine.TranslateAndPreviewAsync(TestChineseTextBox.Text, CancellationToken.None);
        }
        catch (Exception ex)
        {
            OnError(this, ex.Message);
        }
        finally
        {
            TestTranslationButton.IsEnabled = _engine is not null
                && !_settings.DemoMode;
        }
    }

    private async void TestCloudApiButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            TestCloudApiButton.IsEnabled = false;
            var settings = ReadSettings();
            _settingsStore.Save(settings);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await using var localAsr = new LocalOutgoingService();
            await localAsr.StartAsync(timeout.Token);
            using var cloud = new OnlineApiService(settings.ApiEnglishVoice);
            StatusText.Text = "正在测试 MyMemory 翻译与 Edge TTS……";
            var result = await cloud.TestAsync(timeout.Token);
            StatusText.Text = result;
            MessageBox.Show(this, result, "纯 API 模式测试", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            OnError(this, ex.Message);
        }
        finally
        {
            TestCloudApiButton.IsEnabled = _engine is null;
        }
    }

    private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || AsrProviderCombo.SelectedIndex < 0
            || TranslationProviderCombo.SelectedIndex < 0 || TtsProviderCombo.SelectedIndex < 0) return;
        _settings.AsrEngine = AsrProviderFromSelection();
        _settings.UseApiTranslation = TranslationProviderCombo.SelectedIndex == 1;
        _settings.UseApiTts = TtsProviderCombo.SelectedIndex == 1;
        _settings.CloudMode = false;
        _settingsStore.Save(_settings);
        _engine?.UpdateProviders(_settings.UseApiTranslation, _settings.UseApiTts);
        if (_engine is not null)
            StatusText.Text =
                $"已切换：ASR={(_settings.AsrEngine == "sherpa" ? "Sherpa-ONNX" : "Whisper")}，翻译={(_settings.UseApiTranslation ? "API" : "本地")}，TTS={(_settings.UseApiTts ? "API" : "本地")}；重启会话后生效。";
    }

    private string AsrProviderFromSelection()
    {
        var selected = AsrProviderCombo.SelectedItem as ComboBoxItem;
        var tag = selected?.Tag?.ToString()?.Trim().ToLowerInvariant();
        return tag == "sherpa" ? "sherpa" : "whisper";
    }

    private void RefreshDevicesButton_Click(object sender, RoutedEventArgs e) => RefreshDevices();

    private void SubtitleFontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_settings is null) return;
        _settings.SubtitleFontSize = e.NewValue;
        _overlay?.SetFontSize(e.NewValue);
        if (IsLoaded) _settingsStore.Save(_settings);
    }

    private void AdjustOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_overlay is null) return;
        _overlay.SetInteractive(!_overlay.IsInteractive);
        AdjustOverlayButton.Content = _overlay.IsInteractive ? "② 完成并锁定" : "① 解锁移动/缩放";
        if (!_overlay.IsInteractive) SaveOverlayBounds();
    }

    private void RestartElevatedButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings = ReadSettings();
            _settingsStore.Save(_settings);
            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("无法定位 VoiceScreen 可执行文件。");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = true,
                Verb = "runas"
            });
            Application.Current.Shutdown();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            StatusText.Text = "已取消管理员模式重启。";
        }
        catch (Exception ex)
        {
            OnError(this, ex.Message);
        }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void OnSubtitleProduced(object? sender, (string Kind, string Text) item) => _overlay?.AddLine(item.Kind, item.Text);
    private void OnSubtitlePreviewChanged(object? sender, string? text) => _overlay?.SetPreview(text);
    private void OnStatusChanged(object? sender, string text)
    {
        Dispatcher.Invoke(() => StatusText.Text = text);
        _overlay?.SetStatus(text, ok: !text.Contains("错误"));
    }
    private void OnError(object? sender, string text)
    {
        VoiceScreenLog.Error($"engine reported: {text}");
        Dispatcher.Invoke(() => StatusText.Text = "错误：" + text);
        _overlay?.AddLine("status", "错误：" + text);
        _overlay?.SetStatus(text, error: true);
    }

    private async Task StopEngineAsync()
    {
        SaveOverlayBounds();
        _hook.Dispose();
        if (_engine is not null)
        {
            _engine.SubtitleProduced -= OnSubtitleProduced;
            _engine.SubtitlePreviewChanged -= OnSubtitlePreviewChanged;
            _engine.StatusChanged -= OnStatusChanged;
            _engine.Error -= OnError;
            await _engine.DisposeAsync();
            _engine = null;
        }
        _overlay?.Close();
        _overlay = null;
        AdjustOverlayButton.Content = "① 解锁移动/缩放";
        AdjustOverlayButton.IsEnabled = false;
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        TestTranslationButton.IsEnabled = false;
        TestCloudApiButton.IsEnabled = true;
        StatusText.Text = "已停止";
    }

    private bool _shutdownComplete;

    /// <summary>
    /// 关闭流程必须等 <see cref="StopEngineAsync"/> 真正跑完再让窗口消失。
    /// 直接在 <c>async void</c> 里 await 的话，窗口会在第一个 await 处就关掉，
    /// 进程随即退出，<see cref="LocalOutgoingService"/> 里 kill 本地 Python 服务的
    /// 代码可能永远执行不到——结果是残留一个吃着 Whisper + OPUS 模型内存的
    /// python.exe，并且继续占着 18765 端口，下次启动直接失败。
    /// 因此先取消关闭、异步收尾，收尾完成后再重新关闭。
    /// </summary>
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete) return;

        e.Cancel = true;
        try
        {
            SaveOverlayBounds();
            await StopEngineAsync();
        }
        catch (Exception ex)
        {
            VoiceScreenLog.Error("Shutdown cleanup failed", ex);
        }
        finally
        {
            _shutdownComplete = true;
            Close();
        }
    }

    private void SaveOverlayBounds()
    {
        if (_overlay is not null)
        {
            _settings.OverlayLeft = _overlay.Left;
            _settings.OverlayTop = _overlay.Top;
            _settings.OverlayWidth = _overlay.Width;
            _settings.OverlayHeight = _overlay.Height;
        }
        _settingsStore.Save(_settings);
    }
}
