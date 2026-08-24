using System.Diagnostics;
using VoiceScreen.App.Diagnostics;
using VoiceScreen.App.Models;

namespace VoiceScreen.App.Audio;

/// <summary>
/// 监控所选应用的根进程；应用升级或重启导致 PID 改变时自动重新建立进程回环。
/// </summary>
public sealed class ResilientProcessLoopbackCapture : IAsyncDisposable, IDisposable
{
    private readonly ProcessTargetService _targets;
    private readonly string _processName;
    private readonly string _executablePath;
    private readonly int _preferredProcessId;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private ProcessLoopbackCapture? _capture;
    private ProcessAudioTarget? _activeTarget;
    private Task? _supervisor;
    private int _captureFaulted;

    public ResilientProcessLoopbackCapture(
        ProcessTargetService targets,
        string processName,
        string executablePath,
        int preferredProcessId = 0)
    {
        _targets = targets;
        _processName = processName;
        _executablePath = executablePath;
        _preferredProcessId = preferredProcessId;
    }

    public event Func<byte[], CancellationToken, ValueTask>? FrameReady;
    public event EventHandler<string>? StatusChanged;

    public ProcessAudioTarget Start()
    {
        var target = _targets.Resolve(_processName, _executablePath, _preferredProcessId)
            ?? throw new InvalidOperationException(
                $"没有找到要监听的进程“{_processName}”。请先启动目标应用，刷新进程列表后重试。");
        StartCapture(target);
        _supervisor = Task.Run(() => SuperviseAsync(_cts.Token));
        return target;
    }

    private async Task SuperviseAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Interlocked.Exchange(ref _captureFaulted, 0) == 0 && IsProcessAlive(_activeTarget?.ProcessId))
                    continue;

                await StopCurrentCaptureAsync().ConfigureAwait(false);
                StatusChanged?.Invoke(this, $"监听进程 {_processName} 已退出，正在等待它重新启动……");
                while (!cancellationToken.IsCancellationRequested)
                {
                    var replacement = _targets.Resolve(_processName, _executablePath);
                    if (replacement is not null)
                    {
                        try
                        {
                            await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                            try { StartCaptureCore(replacement); }
                            finally { _captureGate.Release(); }
                            StatusChanged?.Invoke(this,
                                $"已重新连接监听进程 {replacement.ProcessName} · PID {replacement.ProcessId}");
                            break;
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }
                        catch (Exception ex)
                        {
                            VoiceScreenLog.Warn($"Reconnecting process loopback failed: {ex.Message}");
                        }
                    }
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 正常停止。
        }
    }

    private void StartCapture(ProcessAudioTarget target)
    {
        _captureGate.Wait();
        try { StartCaptureCore(target); }
        finally { _captureGate.Release(); }
    }

    private void StartCaptureCore(ProcessAudioTarget target)
    {
        var capture = new ProcessLoopbackCapture();
        capture.FrameReady += ForwardFrameAsync;
        capture.Faulted += OnCaptureFaulted;
        capture.StatusChanged += OnCaptureStatusChanged;
        try
        {
            capture.Start(target.ProcessId);
            _capture = capture;
            _activeTarget = target;
            VoiceScreenLog.Info($"Listening target active. process={target.ProcessName} pid={target.ProcessId} path={target.ExecutablePath}");
        }
        catch
        {
            capture.FrameReady -= ForwardFrameAsync;
            capture.Faulted -= OnCaptureFaulted;
            capture.StatusChanged -= OnCaptureStatusChanged;
            capture.Dispose();
            throw;
        }
    }

    private async Task StopCurrentCaptureAsync()
    {
        await _captureGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var capture = _capture;
            _capture = null;
            _activeTarget = null;
            if (capture is null) return;
            capture.FrameReady -= ForwardFrameAsync;
            capture.Faulted -= OnCaptureFaulted;
            capture.StatusChanged -= OnCaptureStatusChanged;
            await capture.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _captureGate.Release();
        }
    }

    private void OnCaptureFaulted(object? sender, string message)
    {
        VoiceScreenLog.Warn($"Process loopback faulted and will reconnect: {message}");
        Interlocked.Exchange(ref _captureFaulted, 1);
    }

    private void OnCaptureStatusChanged(object? sender, string message)
        => StatusChanged?.Invoke(this, message);

    private ValueTask ForwardFrameAsync(byte[] frame, CancellationToken cancellationToken)
        => FrameReady?.Invoke(frame, cancellationToken) ?? ValueTask.CompletedTask;

    private static bool IsProcessAlive(int? processId)
    {
        if (processId is null or <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(processId.Value);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_supervisor is not null)
        {
            try { await _supervisor.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            _supervisor = null;
        }
        await StopCurrentCaptureAsync().ConfigureAwait(false);
        _cts.Dispose();
        _captureGate.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
