using System.Runtime.InteropServices;
using static VoiceScreen.App.Audio.Win32.ProcessLoopbackNative;

namespace VoiceScreen.App.Audio.Win32;

/// <summary>
/// IActivateAudioInterfaceCompletionHandler 的轻量原生实现。
/// 原生对象的第一个字段必须是 vtable；第二个字段保存托管实例的 GCHandle。
/// 同时暴露 IAgileObject，允许系统从 MTA 工作线程回调。
/// </summary>
internal sealed class AudioInterfaceActivationHandler : IDisposable
{
    private static readonly Guid IidIUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IidCompletionHandler = new("41D949AB-9862-444A-80F6-C261334DA5EB");
    private static readonly Guid IidAgileObject = new("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90");

    private static readonly QueryInterfaceDelegate QueryInterfaceCallback = QueryInterfaceThunk;
    private static readonly AddRefDelegate AddRefCallback = AddRefThunk;
    private static readonly ReleaseDelegate ReleaseCallback = ReleaseThunk;
    private static readonly ActivateCompletedDelegate ActivateCompletedCallback = ActivateCompletedThunk;
    private static readonly IntPtr Vtable = CreateVtable();

    private readonly ManualResetEventSlim _done = new(false);
    private GCHandle _selfHandle;
    private IntPtr _nativeObject;
    private int _referenceCount = 1;

    public AudioInterfaceActivationHandler()
    {
        _selfHandle = GCHandle.Alloc(this);
        _nativeObject = Marshal.AllocHGlobal(IntPtr.Size * 2);
        Marshal.WriteIntPtr(_nativeObject, 0, Vtable);
        Marshal.WriteIntPtr(_nativeObject, IntPtr.Size, GCHandle.ToIntPtr(_selfHandle));
    }

    public IntPtr NativeThis => _nativeObject;
    public int ActivationResult { get; private set; } = unchecked((int)0x80004005);
    public IntPtr ActivatedInterface { get; private set; }

    public bool Wait(TimeSpan timeout) => _done.Wait(timeout);

    public void Dispose()
    {
        _done.Dispose();
        if (_nativeObject != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_nativeObject);
            _nativeObject = IntPtr.Zero;
        }
        if (_selfHandle.IsAllocated) _selfHandle.Free();
    }

    private static IntPtr CreateVtable()
    {
        var pointer = Marshal.AllocHGlobal(IntPtr.Size * 4);
        Marshal.WriteIntPtr(pointer, 0, Marshal.GetFunctionPointerForDelegate(QueryInterfaceCallback));
        Marshal.WriteIntPtr(pointer, IntPtr.Size, Marshal.GetFunctionPointerForDelegate(AddRefCallback));
        Marshal.WriteIntPtr(pointer, IntPtr.Size * 2, Marshal.GetFunctionPointerForDelegate(ReleaseCallback));
        Marshal.WriteIntPtr(pointer, IntPtr.Size * 3, Marshal.GetFunctionPointerForDelegate(ActivateCompletedCallback));
        return pointer;
    }

    private static AudioInterfaceActivationHandler? GetInstance(IntPtr self)
    {
        if (self == IntPtr.Zero) return null;
        var handlePointer = Marshal.ReadIntPtr(self, IntPtr.Size);
        if (handlePointer == IntPtr.Zero) return null;
        return GCHandle.FromIntPtr(handlePointer).Target as AudioInterfaceActivationHandler;
    }

    private static int QueryInterfaceThunk(IntPtr self, IntPtr iidPointer, out IntPtr result)
    {
        var iid = Marshal.PtrToStructure<Guid>(iidPointer);
        if (iid == IidIUnknown || iid == IidCompletionHandler || iid == IidAgileObject)
        {
            result = self;
            _ = AddRefThunk(self);
            return SOk;
        }
        result = IntPtr.Zero;
        return unchecked((int)0x80004002); // E_NOINTERFACE
    }

    private static uint AddRefThunk(IntPtr self)
    {
        var instance = GetInstance(self);
        return instance is null ? 0 : (uint)Interlocked.Increment(ref instance._referenceCount);
    }

    private static uint ReleaseThunk(IntPtr self)
    {
        var instance = GetInstance(self);
        if (instance is null) return 0;
        var count = Interlocked.Decrement(ref instance._referenceCount);
        return (uint)Math.Max(0, count);
    }

    private static int ActivateCompletedThunk(IntPtr self, IntPtr activationOperation)
    {
        var instance = GetInstance(self);
        if (instance is null) return unchecked((int)0x80004005);

        try
        {
            var vtable = Marshal.ReadIntPtr(activationOperation);
            var method = Marshal.ReadIntPtr(vtable, IntPtr.Size * 3);
            var getResult = Marshal.GetDelegateForFunctionPointer<GetActivateResultDelegate>(method);
            var callResult = getResult(activationOperation, out var activationResult, out var activatedInterface);
            instance.ActivationResult = callResult == SOk ? activationResult : callResult;
            instance.ActivatedInterface = activatedInterface;
        }
        catch
        {
            instance.ActivationResult = unchecked((int)0x80004005);
        }
        finally
        {
            instance._done.Set();
        }
        return SOk;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceDelegate(IntPtr self, IntPtr iid, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint AddRefDelegate(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseDelegate(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ActivateCompletedDelegate(IntPtr self, IntPtr operation);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetActivateResultDelegate(IntPtr self, out int activationResult, out IntPtr activatedInterface);
}
