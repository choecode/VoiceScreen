using System.Diagnostics;
using System.Runtime.InteropServices;
using VoiceScreen.App.Models;

namespace VoiceScreen.App.Audio;

/// <summary>枚举和重新解析可被 Windows Application Loopback 捕获的桌面进程。</summary>
public sealed class ProcessTargetService
{
    private static readonly string[] PreferredNames =
    {
        "Discord", "DiscordPTB", "DiscordCanary", "chrome", "msedge", "firefox",
        "Spotify", "vlc", "mpv", "Teams", "ms-teams", "Zoom"
    };

    public IReadOnlyList<ProcessAudioTarget> GetRunningTargets()
        => SnapshotTargets(onlyInteractive: true)
            .OrderBy(target => Priority(target.ProcessName))
            .ThenBy(target => string.IsNullOrWhiteSpace(target.ProductName) ? target.ProcessName : target.ProductName,
                StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(target => target.ProcessId)
            .ToArray();

    public ProcessAudioTarget? Resolve(string processName, string executablePath, int preferredProcessId = 0)
    {
        if (string.IsNullOrWhiteSpace(processName) && string.IsNullOrWhiteSpace(executablePath)
            && preferredProcessId <= 0) return null;
        var targets = SnapshotTargets(onlyInteractive: false);
        if (preferredProcessId > 0)
        {
            var preferred = targets.FirstOrDefault(target => target.ProcessId == preferredProcessId);
            if (preferred is not null
                && (string.IsNullOrWhiteSpace(executablePath) || string.Equals(preferred.ExecutablePath,
                    executablePath, StringComparison.OrdinalIgnoreCase))
                && (string.IsNullOrWhiteSpace(processName) || string.Equals(preferred.ProcessName,
                    processName, StringComparison.OrdinalIgnoreCase)))
                return preferred;
        }
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            var exact = targets.FirstOrDefault(target =>
                string.Equals(target.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
        }

        return targets.FirstOrDefault(target =>
            string.Equals(target.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
    }

    public static ProcessAudioTarget? FindBest(
        IEnumerable<ProcessAudioTarget> targets,
        string savedProcessName,
        string savedExecutablePath,
        int savedProcessId = 0)
    {
        var available = targets.ToArray();
        if (savedProcessId > 0)
        {
            var byId = available.FirstOrDefault(target => target.ProcessId == savedProcessId);
            if (byId is not null) return byId;
        }
        if (!string.IsNullOrWhiteSpace(savedExecutablePath))
        {
            var exact = available.FirstOrDefault(target => string.Equals(
                target.ExecutablePath, savedExecutablePath, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
        }
        if (!string.IsNullOrWhiteSpace(savedProcessName))
        {
            var byName = available.FirstOrDefault(target => string.Equals(
                target.ProcessName, savedProcessName, StringComparison.OrdinalIgnoreCase));
            if (byName is not null) return byName;
        }

        return available.FirstOrDefault();
    }

    private static IReadOnlyList<ProcessAudioTarget> SnapshotTargets(bool onlyInteractive)
    {
        var candidates = new List<Candidate>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || process.HasExited) continue;
                    var executablePath = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(executablePath)) continue;
                    var name = process.ProcessName;
                    var title = process.MainWindowTitle?.Trim() ?? string.Empty;
                    var product = process.MainModule?.FileVersionInfo.ProductName?.Trim() ?? string.Empty;
                    candidates.Add(new Candidate(
                        process.Id,
                        GetParentProcessId(process.Handle),
                        SafeStartTime(process),
                        name,
                        executablePath,
                        title,
                        product));
                }
                catch
                {
                    // 受保护进程或正在退出的进程不可捕获，忽略即可。
                }
            }
        }

        var targets = new List<ProcessAudioTarget>();
        foreach (var group in candidates.GroupBy(candidate => candidate.ExecutablePath, StringComparer.OrdinalIgnoreCase))
        {
            var groupCandidates = group.ToArray();
            if (onlyInteractive
                && !groupCandidates.Any(candidate => !string.IsNullOrWhiteSpace(candidate.WindowTitle))
                && !groupCandidates.Any(candidate => IsPreferred(candidate.ProcessName)))
                continue;

            var candidateIds = groupCandidates.Select(candidate => candidate.Id).ToHashSet();
            var roots = groupCandidates
                .Where(candidate => !candidateIds.Contains(candidate.ParentId))
                .OrderBy(candidate => candidate.StartTime)
                .ThenBy(candidate => candidate.Id)
                .ToArray();
            if (roots.Length == 0)
                roots = new[] { groupCandidates.OrderBy(candidate => candidate.StartTime).ThenBy(candidate => candidate.Id).First() };

            foreach (var root in roots)
            {
                var titled = !string.IsNullOrWhiteSpace(root.WindowTitle)
                    ? root
                    : groupCandidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.WindowTitle));
                targets.Add(new ProcessAudioTarget(
                    root.Id,
                    root.ProcessName,
                    root.ExecutablePath,
                    titled?.WindowTitle ?? string.Empty,
                    root.ProductName));
            }
        }

        return targets;
    }

    private static bool IsPreferred(string processName)
        => PreferredNames.Any(name => string.Equals(name, processName, StringComparison.OrdinalIgnoreCase));

    private static int Priority(string processName)
    {
        for (var index = 0; index < PreferredNames.Length; index++)
            if (string.Equals(PreferredNames[index], processName, StringComparison.OrdinalIgnoreCase)) return index;
        return PreferredNames.Length;
    }

    private static DateTime SafeStartTime(Process process)
    {
        try { return process.StartTime; }
        catch { return DateTime.MaxValue; }
    }

    private static int GetParentProcessId(IntPtr processHandle)
    {
        var status = NtQueryInformationProcess(processHandle, 0, out var info,
            Marshal.SizeOf<ProcessBasicInformation>(), out _);
        return status == 0 ? unchecked((int)info.InheritedFromUniqueProcessId.ToInt64()) : 0;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass,
        out ProcessBasicInformation processInformation, int processInformationLength, out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2A;
        public IntPtr Reserved2B;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    private sealed record Candidate(
        int Id,
        int ParentId,
        DateTime StartTime,
        string ProcessName,
        string ExecutablePath,
        string WindowTitle,
        string ProductName);
}
