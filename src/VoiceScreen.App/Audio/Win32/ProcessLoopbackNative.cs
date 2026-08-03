using System.Runtime.InteropServices;

namespace VoiceScreen.App.Audio.Win32;

/// <summary>Windows 进程级 WASAPI loopback 所需的原生声明。</summary>
internal static class ProcessLoopbackNative
{
    // audioclientactivationparams.h 中定义的虚拟音频设备路径。
    public const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";

    public static readonly Guid IidIAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    public static readonly Guid IidIAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    public const int AudioClientActivationTypeProcessLoopback = 1;
    public const int IncludeTargetProcessTree = 0;
    public const int AudclntSharemodeShared = 0;

    public const uint AudclntStreamflagsLoopback = 0x00020000;
    public const uint AudclntStreamflagsEventCallback = 0x00040000;
    public const uint AudclntStreamflagsAutoConvertPcm = 0x80000000;
    public const uint AudclntStreamflagsSrcDefaultQuality = 0x08000000;
    public const uint AudclntBufferflagsSilent = 0x00000002;
    public const int SOk = 0;

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    public static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IntPtr completionHandler,
        out IntPtr activationOperation);

    [DllImport("ole32.dll")]
    public static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    public static extern void CoUninitialize();

    public const uint CoInitMultithreaded = 0;

    public static void ReleaseComPointer(ref IntPtr instance)
    {
        var value = instance;
        instance = IntPtr.Zero;
        if (value == IntPtr.Zero) return;

        var vtable = Marshal.ReadIntPtr(value);
        var releasePointer = Marshal.ReadIntPtr(vtable, IntPtr.Size * 2);
        var release = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(releasePointer);
        _ = release(value);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseDelegate(IntPtr self);
}
