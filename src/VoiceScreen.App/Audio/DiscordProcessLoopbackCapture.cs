using System.Runtime.InteropServices;
using NAudio.Wave;
using VoiceScreen.App.Audio.Win32;
using VoiceScreen.App.Diagnostics;
using static VoiceScreen.App.Audio.Win32.ProcessLoopbackNative;

namespace VoiceScreen.App.Audio;

/// <summary>只捕获指定 Discord 进程树产生的播放音频。</summary>
public sealed class DiscordProcessLoopbackCapture : IAsyncDisposable, IDisposable
{
    private const int InputSampleRate = 44100;
    private const short InputChannels = 2;
    private const short InputBits = 16;
    private const short InputBlockAlign = InputChannels * (InputBits / 8);

    private StreamingPcm16Pump? _pump;
    private IntPtr _audioClient;
    private IntPtr _captureClient;
    private Thread? _captureThread;
    private CancellationTokenSource? _cts;
    private AutoResetEvent? _sampleReady;

    public event Func<byte[], CancellationToken, ValueTask>? FrameReady;

    public void Start(int targetProcessId)
    {
        if (targetProcessId <= 0) throw new ArgumentOutOfRangeException(nameof(targetProcessId));
        if (_audioClient != IntPtr.Zero) throw new InvalidOperationException("Discord 音频捕获已经启动。");

        try
        {
            Activate(targetProcessId);
            InitializeStream();

            _pump = new StreamingPcm16Pump(new WaveFormat(InputSampleRate, InputBits, InputChannels));
            _pump.FrameReady += ForwardFrameAsync;
            _pump.Start();

            _cts = new CancellationTokenSource();
            _captureThread = new Thread(() => CaptureLoop(_cts.Token))
            {
                IsBackground = true,
                Name = "VoiceScreen.DiscordProcessLoopback"
            };
            _captureThread.Start();
            VoiceScreenLog.Info($"Discord-only process loopback started. rootPid={targetProcessId}");
        }
        catch
        {
            CleanupNativeClients();
            _sampleReady?.Dispose();
            _sampleReady = null;
            throw;
        }
    }

    private void Activate(int processId)
    {
        using var handler = new AudioInterfaceActivationHandler();
        var propVariant = BuildActivationPropVariant(processId, out var activationData);
        IntPtr operation = IntPtr.Zero;
        try
        {
            var iid = IidIAudioClient;
            var result = ActivateAudioInterfaceAsync(
                VirtualAudioDeviceProcessLoopback,
                ref iid,
                propVariant,
                handler.NativeThis,
                out operation);
            ThrowIfFailed(result, "ActivateAudioInterfaceAsync");

            if (!handler.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("等待 Windows 激活 Discord 进程音频接口超时。");

            ThrowIfFailed(handler.ActivationResult, "激活 Discord 进程音频接口");
            _audioClient = handler.ActivatedInterface;
            if (_audioClient == IntPtr.Zero)
                throw new InvalidOperationException("Windows 返回了空的 Discord 音频接口。");
        }
        finally
        {
            ReleaseComPointer(ref operation);
            Marshal.FreeHGlobal(propVariant);
            Marshal.FreeHGlobal(activationData);
        }
    }

    private void InitializeStream()
    {
        _sampleReady = new AutoResetEvent(false);
        var format = new WaveFormatEx
        {
            FormatTag = 1, // WAVE_FORMAT_PCM
            Channels = InputChannels,
            SamplesPerSec = InputSampleRate,
            AvgBytesPerSec = InputSampleRate * InputBlockAlign,
            BlockAlign = InputBlockAlign,
            BitsPerSample = InputBits,
            ExtraSize = 0
        };
        var formatPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());
        try
        {
            Marshal.StructureToPtr(format, formatPointer, false);
            var vtable = Marshal.ReadIntPtr(_audioClient);
            var initialize = GetMethod<InitializeDelegate>(vtable, 3);
            // 与微软 ApplicationLoopback 官方示例保持一致。
            const uint flags = AudclntStreamflagsLoopback
                | AudclntStreamflagsEventCallback
                | AudclntStreamflagsAutoConvertPcm;
            ThrowIfFailed(initialize(_audioClient, AudclntSharemodeShared, flags, 0, 0, formatPointer, IntPtr.Zero),
                "IAudioClient.Initialize");

            var getService = GetMethod<GetServiceDelegate>(vtable, 14);
            var iid = IidIAudioCaptureClient;
            ThrowIfFailed(getService(_audioClient, ref iid, out _captureClient),
                "IAudioClient.GetService(IAudioCaptureClient)");

            var setEventHandle = GetMethod<SetEventHandleDelegate>(vtable, 13);
            ThrowIfFailed(setEventHandle(_audioClient, _sampleReady.SafeWaitHandle.DangerousGetHandle()),
                "IAudioClient.SetEventHandle");

            var start = GetMethod<StartStopDelegate>(vtable, 10);
            ThrowIfFailed(start(_audioClient), "IAudioClient.Start");
        }
        finally
        {
            Marshal.FreeHGlobal(formatPointer);
        }
    }

    private void CaptureLoop(CancellationToken token)
    {
        var comInitialized = CoInitializeEx(IntPtr.Zero, CoInitMultithreaded) >= 0;
        try
        {
            var vtable = Marshal.ReadIntPtr(_captureClient);
            var getBuffer = GetMethod<GetBufferDelegate>(vtable, 3);
            var releaseBuffer = GetMethod<ReleaseBufferDelegate>(vtable, 4);
            var getNextPacketSize = GetMethod<GetNextPacketSizeDelegate>(vtable, 5);
            var buffer = new byte[64 * 1024];

            while (!token.IsCancellationRequested)
            {
                if (_sampleReady?.WaitOne(100) != true) continue;

                while (!token.IsCancellationRequested)
                {
                    var result = getNextPacketSize(_captureClient, out var framesInPacket);
                    if (result < 0)
                    {
                        VoiceScreenLog.Warn($"Discord process loopback GetNextPacketSize failed: 0x{result:X8}");
                        break;
                    }
                    if (framesInPacket == 0) break;

                    result = getBuffer(_captureClient, out var data, out var frames, out var flags, out _, out _);
                    if (result < 0)
                    {
                        VoiceScreenLog.Warn($"Discord process loopback GetBuffer failed: 0x{result:X8}");
                        break;
                    }

                    try
                    {
                        var byteCount = checked((int)frames * InputBlockAlign);
                        if (buffer.Length < byteCount) Array.Resize(ref buffer, byteCount);
                        if ((flags & AudclntBufferflagsSilent) != 0 || data == IntPtr.Zero)
                            Array.Clear(buffer, 0, byteCount);
                        else
                            Marshal.Copy(data, buffer, 0, byteCount);
                        _pump?.AddSamples(buffer, byteCount);
                    }
                    finally
                    {
                        _ = releaseBuffer(_captureClient, frames);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            VoiceScreenLog.Error("Discord process loopback capture thread stopped unexpectedly", ex);
        }
        finally
        {
            if (comInitialized) CoUninitialize();
        }
    }

    private ValueTask ForwardFrameAsync(byte[] frame, CancellationToken cancellationToken)
        => FrameReady?.Invoke(frame, cancellationToken) ?? ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _sampleReady?.Set();
        if (_captureThread is not null)
        {
            _captureThread.Join(TimeSpan.FromSeconds(2));
            _captureThread = null;
        }
        _cts?.Dispose();
        _cts = null;

        StopAudioClient();

        if (_pump is not null)
        {
            _pump.FrameReady -= ForwardFrameAsync;
            await _pump.DisposeAsync();
            _pump = null;
        }

        CleanupNativeClients();
        _sampleReady?.Dispose();
        _sampleReady = null;
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private void StopAudioClient()
    {
        if (_audioClient == IntPtr.Zero) return;
        try
        {
            var stop = GetMethod<StartStopDelegate>(Marshal.ReadIntPtr(_audioClient), 11);
            _ = stop(_audioClient);
        }
        catch (Exception ex)
        {
            VoiceScreenLog.Warn($"Stopping Discord process loopback failed: {ex.Message}");
        }
    }

    private void CleanupNativeClients()
    {
        // CaptureClient 由 AudioClient 创建，必须先释放子接口。
        ReleaseComPointer(ref _captureClient);
        ReleaseComPointer(ref _audioClient);
    }

    private static IntPtr BuildActivationPropVariant(int processId, out IntPtr activationData)
    {
        var parameters = new AudioClientActivationParams
        {
            ActivationType = AudioClientActivationTypeProcessLoopback,
            ProcessLoopbackParams = new AudioClientProcessLoopbackParams
            {
                TargetProcessId = (uint)processId,
                ProcessLoopbackMode = IncludeTargetProcessTree
            }
        };
        activationData = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
        Marshal.StructureToPtr(parameters, activationData, false);

        var variant = new PropVariantBlob
        {
            VariantType = 0x41, // VT_BLOB
            Blob = new Blob
            {
                Size = (uint)Marshal.SizeOf<AudioClientActivationParams>(),
                Data = activationData
            }
        };
        var variantPointer = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        Marshal.StructureToPtr(variant, variantPointer, false);
        return variantPointer;
    }

    private static T GetMethod<T>(IntPtr vtable, int slot) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, IntPtr.Size * slot));

    private static void ThrowIfFailed(int result, string operation)
    {
        if (result < 0) throw new InvalidOperationException($"{operation} 失败，HRESULT=0x{result:X8}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientProcessLoopbackParams
    {
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        public int ActivationType;
        public AudioClientProcessLoopbackParams ProcessLoopbackParams;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public uint Size;
        public IntPtr Data;
    }

    // PROPVARIANT 的 union 在 x64 上从偏移 8 开始，BLOB 必须内嵌，不能放 BLOB*。
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariantBlob
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(8)] public Blob Blob;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx
    {
        public short FormatTag;
        public short Channels;
        public int SamplesPerSec;
        public int AvgBytesPerSec;
        public short BlockAlign;
        public short BitsPerSample;
        public short ExtraSize;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int InitializeDelegate(IntPtr self, int shareMode, uint flags, long bufferDuration,
        long periodicity, IntPtr format, IntPtr sessionGuid);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetServiceDelegate(IntPtr self, ref Guid iid, out IntPtr service);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetEventHandleDelegate(IntPtr self, IntPtr eventHandle);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int StartStopDelegate(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetNextPacketSizeDelegate(IntPtr self, out uint frames);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBufferDelegate(IntPtr self, out IntPtr data, out uint frames, out uint flags,
        out ulong devicePosition, out ulong qpcPosition);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReleaseBufferDelegate(IntPtr self, uint frames);
}
