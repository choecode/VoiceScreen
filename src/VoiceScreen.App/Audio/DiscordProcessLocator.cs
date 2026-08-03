using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VoiceScreen.App.Audio;

/// <summary>定位 Discord 桌面客户端的根进程，以便捕获完整 Electron 进程树。</summary>
public static class DiscordProcessLocator
{
    private static readonly string[] CandidateProcessNames = { "Discord", "DiscordPTB", "DiscordCanary" };

    public static int? FindMainProcessId()
    {
        foreach (var processName in CandidateProcessNames)
        {
            var candidates = new List<(int Id, int ParentId, DateTime StartTime)>();
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        var filePath = process.MainModule?.FileName;
                        if (string.IsNullOrWhiteSpace(filePath) || !IsDiscordExecutable(filePath, processName)) continue;
                        candidates.Add((process.Id, GetParentProcessId(process.Handle), process.StartTime));
                    }
                    catch
                    {
                        // 进程可能正在退出，跳过即可。
                    }
                }
            }

            if (candidates.Count == 0) continue;
            var candidateIds = candidates.Select(item => item.Id).ToHashSet();
            // Electron 主进程的父进程不是 Discord.exe；所有 renderer/audio 子进程都指向主进程。
            var root = candidates
                .Where(item => !candidateIds.Contains(item.ParentId))
                .OrderBy(item => item.StartTime)
                .FirstOrDefault();
            if (root.Id > 0) return root.Id;

            // 极端情况下父进程信息取不到，最早启动的 Discord 仍是最合理的根节点。
            return candidates.OrderBy(item => item.StartTime).ThenBy(item => item.Id).First().Id;
        }
        return null;
    }

    private static bool IsDiscordExecutable(string filePath, string processName)
    {
        if (!Path.GetFileName(filePath).Equals(processName + ".exe", StringComparison.OrdinalIgnoreCase)) return false;
        var directory = Path.GetDirectoryName(filePath) ?? string.Empty;
        return directory.Contains(Path.DirectorySeparatorChar + "app-", StringComparison.OrdinalIgnoreCase)
            || directory.Contains(Path.DirectorySeparatorChar + "app" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
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
}
