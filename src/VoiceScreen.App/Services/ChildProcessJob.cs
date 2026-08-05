using System.Diagnostics;
using System.Runtime.InteropServices;
using VoiceScreen.App.Diagnostics;

namespace VoiceScreen.App.Services;

/// <summary>
/// 用 Win32 Job Object 兜住本地 Python 推理服务的生命周期。
///
/// 正常退出路径靠 <see cref="LocalOutgoingService.DisposeAsync"/> 里的
/// <c>Process.Kill(entireProcessTree: true)</c>，但那条路径在崩溃、任务管理器强杀、
/// 或 WPF 关闭流程被打断时都不会执行。一旦漏掉，残留的 python.exe 会一直占着
/// 两套 Whisper + 三套 OPUS 模型的内存和 18765 端口，下次启动直接失败。
///
/// 把子进程放进带 <c>KILL_ON_JOB_CLOSE</c> 的 job：句柄随进程消亡而关闭，
/// 内核负责连带杀掉子进程，不依赖任何托管代码跑到。
/// </summary>
internal sealed class ChildProcessJob : IDisposable
{
    private IntPtr _handle;

    private ChildProcessJob(IntPtr handle) => _handle = handle;

    /// <summary>创建 job；系统不支持时返回 null，调用方退回到只依赖显式 Kill。</summary>
    public static ChildProcessJob? TryCreate()
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            VoiceScreenLog.Warn($"CreateJobObject failed; child process cleanup falls back to explicit kill. win32={Marshal.GetLastWin32Error()}");
            return null;
        }

        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = LimitKillOnJobClose
            }
        };

        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(information, buffer, false);
            if (!SetInformationJobObject(handle, ExtendedLimitInformationClass, buffer, (uint)size))
            {
                VoiceScreenLog.Warn($"SetInformationJobObject failed; child process cleanup falls back to explicit kill. win32={Marshal.GetLastWin32Error()}");
                CloseHandle(handle);
                return null;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return new ChildProcessJob(handle);
    }

    public void Assign(Process process)
    {
        if (_handle == IntPtr.Zero) return;
        if (!AssignProcessToJobObject(_handle, process.Handle))
            VoiceScreenLog.Warn($"AssignProcessToJobObject failed; child process cleanup falls back to explicit kill. win32={Marshal.GetLastWin32Error()}");
        else
            VoiceScreenLog.Info($"Local service process {process.Id} assigned to kill-on-close job");
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        CloseHandle(_handle);
        _handle = IntPtr.Zero;
    }

    private const int ExtendedLimitInformationClass = 9;
    private const uint LimitKillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
