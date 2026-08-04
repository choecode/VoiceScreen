using System.Windows;
using VoiceScreen.App.Diagnostics;
using VoiceScreen.App.Models;
using VoiceScreen.App.Services;

namespace VoiceScreen.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        VoiceScreenLog.Info($"=== VoiceScreen.App process started. pid={Environment.ProcessId} args=[{string.Join(' ', e.Args)}]");

        var isSmoke = e.Args.Any(a => string.Equals(a, "--smoke", StringComparison.OrdinalIgnoreCase));
        var autoStart = e.Args.Any(a => string.Equals(a, "--auto-start", StringComparison.OrdinalIgnoreCase));

        if (!isSmoke)
        {
            // 正常 UI 模式：手动创建 MainWindow（不在 App.xaml 用 StartupUri，避免在用户
            // 机器某些环境下 WPF 自动创建窗口时调 OLE RegisterDragDrop 触发 BEX64 fastfail）。
            // 手动创建时我们在 MainWindow.OnSourceInitialized 里撤销 OLE 注册。
            var main = new MainWindow();
            MainWindow = main;
            if (autoStart)
                main.Loaded += (_, _) => Dispatcher.InvokeAsync(AutoStartPipelineAsync);
            main.Show();
        }
        else
        {
            // smoke 模式：不创建任何 WPF 窗口，避免 WPF 启动时 OLE 注册这条路径。
            _ = RunSmokeAsync(e.Args);
        }
    }

    /// <summary>
    /// --auto-start 配套：等 UI 完全就绪后，自动点"启动"，跑 5 秒，然后自动点"停止"再退出。
    /// 用来在没有 UI 自动化时验证完整的 UI 模式 + 引擎生命周期。
    /// </summary>
    private async Task AutoStartPipelineAsync()
    {
        if (MainWindow is not MainWindow main) return;
        VoiceScreenLog.Info("[auto-start] kicking off engine");
        try { main.AutoStart(); }
        catch (Exception ex) { VoiceScreenLog.Error("[auto-start] start failed", ex); return; }
        await Task.Delay(TimeSpan.FromSeconds(5));
        VoiceScreenLog.Info("[auto-start] stopping engine");
        try { main.AutoStop(); }
        catch (Exception ex) { VoiceScreenLog.Error("[auto-start] stop failed", ex); }
        await Task.Delay(500);
        VoiceScreenLog.Info("[auto-start] shutting down app");
        Shutdown(0);
    }

    /// <summary>
    /// 无 UI 的端到端冒烟测试：构造引擎 → StartAsync → 等 5 秒让 Discord 音频流进来 →
    /// DisposeAsync → 退出。专门用来在没有 UI 自动化时验证整条链路。
    /// </summary>
    private async Task RunSmokeAsync(string[] args)
    {
        VoiceScreenLog.Info("[smoke] starting smoke test");
        try
        {
            var settings = new SettingsStore().Load();
            settings.UseProcessLoopback = true;

            await using var engine = new TranslationEngine(settings);
            engine.StatusChanged += (_, msg) => VoiceScreenLog.Info($"[smoke] status: {msg}");
            engine.Error += (_, msg) => VoiceScreenLog.Error($"[smoke] engine error: {msg}");

            try
            {
                await engine.StartAsync(CancellationToken.None);
                VoiceScreenLog.Info("[smoke] engine started, sleeping 5s to let audio flow");
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                VoiceScreenLog.Error("[smoke] engine.StartAsync threw", ex);
            }

            VoiceScreenLog.Info("[smoke] engine stopping");
        }
        catch (Exception ex)
        {
            VoiceScreenLog.Error("[smoke] outer failure", ex);
        }
        finally
        {
            VoiceScreenLog.Info("[smoke] smoke test finished, exiting process");
            await Task.Delay(200);
            Shutdown(0);
        }
    }
}
