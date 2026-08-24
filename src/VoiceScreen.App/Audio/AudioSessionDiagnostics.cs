using System.Diagnostics;
using NAudio.CoreAudioApi;
using VoiceScreen.App.Diagnostics;

namespace VoiceScreen.App.Audio;

internal static class AudioSessionDiagnostics
{
    public static void LogForProcessName(int rootProcessId)
    {
        string processName;
        try
        {
            using var root = Process.GetProcessById(rootProcessId);
            processName = root.ProcessName;
        }
        catch
        {
            return;
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                var manager = device.AudioSessionManager;
                var sessionCount = 0;
                var activeCount = 0;
                var targetCount = 0;
                try
                {
                    manager.RefreshSessions();
                    for (var index = 0; index < manager.Sessions.Count; index++)
                    {
                        sessionCount++;
                        var session = manager.Sessions[index];
                        try
                        {
                            var pid = unchecked((int)session.GetProcessID);
                            using var process = pid > 0 ? Process.GetProcessById(pid) : null;
                            var name = process?.ProcessName ?? "system";
                            var state = session.State;
                            var peak = session.AudioMeterInformation.MasterPeakValue;
                            if (string.Equals(state.ToString(), "AudioSessionStateActive",
                                    StringComparison.OrdinalIgnoreCase) || peak > 0)
                            {
                                activeCount++;
                                VoiceScreenLog.Info(
                                    $"Active audio session: endpoint={device.FriendlyName} process={name} pid={pid} state={state} mute={session.SimpleAudioVolume.Mute} volume={session.SimpleAudioVolume.Volume:F2} peak={peak:F4}");
                            }
                            if (process is null || !string.Equals(name, processName,
                                    StringComparison.OrdinalIgnoreCase)) continue;
                            targetCount++;
                            VoiceScreenLog.Info(
                                $"Target audio session: endpoint={device.FriendlyName} pid={pid} state={state} mute={session.SimpleAudioVolume.Mute} volume={session.SimpleAudioVolume.Volume:F2} peak={peak:F4}");
                        }
                        catch (Exception ex)
                        {
                            // 会话或进程可能在枚举期间退出。
                            VoiceScreenLog.Warn($"Reading one audio session failed: {ex.Message}");
                        }
                    }
                }
                finally
                {
                    VoiceScreenLog.Info(
                        $"Audio session summary: endpoint={device.FriendlyName} sessions={sessionCount} active={activeCount} targetMatches={targetCount}");
                    manager.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            VoiceScreenLog.Warn($"Reading target audio sessions failed: {ex.Message}");
        }
    }
}
