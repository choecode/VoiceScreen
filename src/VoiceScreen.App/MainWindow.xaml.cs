using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
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
        DemoModeCheckBox.IsChecked = _settings.DemoMode;
        MonitorTranslatedSpeechCheckBox.IsChecked = _settings.MonitorTranslatedSpeech;
        UseProcessLoopbackCheckBox.IsChecked = true;
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
            DemoMode = DemoModeCheckBox.IsChecked == true,
            UseProcessLoopback = true,
            MicrophoneDeviceId = (MicrophoneCombo.SelectedItem as AudioDeviceOption)?.Id ?? string.Empty,
            DiscordOutputDeviceId = (DiscordOutputCombo.SelectedItem as AudioDeviceOption)?.Id ?? string.Empty,
            CableRenderDeviceId = (CableRenderCombo.SelectedItem as AudioDeviceOption)?.Id ?? string.Empty,
            MonitorRenderDeviceId = (MonitorOutputCombo.SelectedItem as AudioDeviceOption)?.Id ?? string.Empty,
            MonitorTranslatedSpeech = MonitorTranslatedSpeechCheckBox.IsChecked == true,
            EnglishVoiceName = (EnglishVoiceCombo.SelectedItem as SpeechVoiceOption)?.Id ?? string.Empty,
            MaxSubtitleLines = _settings.MaxSubtitleLines,
            OverlayLeft = _settings.OverlayLeft,
            OverlayTop = _settings.OverlayTop,
            OverlayWidth = _settings.OverlayWidth,
            OverlayHeight = _settings.OverlayHeight
        };
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings = ReadSettings();
            _settingsStore.Save(_settings);
            _overlay = new OverlayWindow(_settings.MaxSubtitleLines, _settings.OverlayLeft, _settings.OverlayTop,
                _settings.OverlayWidth, _settings.OverlayHeight);
            _overlay.Show();
            _engine = new TranslationEngine(_settings);
            _engine.SubtitleProduced += OnSubtitleProduced;
            _engine.StatusChanged += OnStatusChanged;
            _engine.Error += OnError;
            await _engine.StartAsync(CancellationToken.None);
            _hook.Start();
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            TestTranslationButton.IsEnabled = !_settings.DemoMode;
            AdjustOverlayButton.IsEnabled = true;
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

    private void RefreshDevicesButton_Click(object sender, RoutedEventArgs e) => RefreshDevices();

    private void AdjustOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_overlay is null) return;
        _overlay.SetInteractive(!_overlay.IsInteractive);
        AdjustOverlayButton.Content = _overlay.IsInteractive ? "锁定悬浮窗" : "调整悬浮窗";
        if (!_overlay.IsInteractive) SaveOverlayBounds();
    }

    private void OnSubtitleProduced(object? sender, (string Kind, string Text) item) => _overlay?.AddLine(item.Kind, item.Text);
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
            _engine.StatusChanged -= OnStatusChanged;
            _engine.Error -= OnError;
            await _engine.DisposeAsync();
            _engine = null;
        }
        _overlay?.Close();
        _overlay = null;
        AdjustOverlayButton.Content = "调整悬浮窗";
        AdjustOverlayButton.IsEnabled = false;
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        TestTranslationButton.IsEnabled = false;
        StatusText.Text = "已停止";
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        SaveOverlayBounds();
        await StopEngineAsync();
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
