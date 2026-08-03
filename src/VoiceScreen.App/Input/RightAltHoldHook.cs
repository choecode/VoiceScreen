using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using VoiceScreen.App.Diagnostics;

namespace VoiceScreen.App.Input;

public sealed class RightAltHoldHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100;
    private const int WmKeyup = 0x0101;
    private const int WmSyskeydown = 0x0104;
    private const int WmSyskeyup = 0x0105;
    private const int VkRmenu = 0xA5;
    private const int VkPrior = 0x21; // Page Up
    private const int VkNext = 0x22; // Page Down
    private const int VkMenu = 0x12;
    private const int WmInput = 0x00FF;
    private const uint RidInput = 0x10000003;
    private const uint RimTypeKeyboard = 1;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevRemove = 0x00000001;
    private const ushort RiKeyBreak = 0x0001;
    private const ushort RiKeyE0 = 0x0002;
    private readonly LowLevelKeyboardProc _callback;
    private readonly object _stateGate = new();
    private IntPtr _hook;
    private Timer? _pollTimer;
    private SynchronizationContext? _eventContext;
    private HwndSource? _windowSource;
    private bool _rawInputRegistered;
    private bool _isDown;
    private bool _pageUpDown;
    private bool _pageDownDown;

    public RightAltHoldHook()
    {
        _callback = HookCallback;
    }

    public event EventHandler? Pressed;
    public event EventHandler? Released;
    public event EventHandler? PageUpPressed;
    public event EventHandler? PageDownPressed;

    public void Start(IntPtr windowHandle)
    {
        if (_hook != IntPtr.Zero || _pollTimer is not null) return;
        _eventContext = SynchronizationContext.Current;
        RegisterRawKeyboard(windowHandle);
        _pollTimer = new Timer(_ => PollGlobalKeyStates(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(12));
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule!;
        _hook = SetWindowsHookEx(WhKeyboardLl, _callback, GetModuleHandle(module.ModuleName), 0);
        if (_hook == IntPtr.Zero)
            VoiceScreenLog.Warn($"Low-level keyboard hook unavailable, async key polling remains active. win32={Marshal.GetLastWin32Error()}");
        else
            VoiceScreenLog.Info("Global hotkeys started: raw input sink + low-level hook + async key polling fallback");
    }

    private void RegisterRawKeyboard(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            VoiceScreenLog.Warn("Raw keyboard input sink unavailable: main window handle is empty");
            return;
        }
        var devices = new[]
        {
            new RawInputDevice
            {
                UsagePage = 0x01,
                Usage = 0x06,
                Flags = RidevInputSink,
                Target = windowHandle
            }
        };
        if (!RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RawInputDevice>()))
        {
            VoiceScreenLog.Warn($"Raw keyboard input sink registration failed. win32={Marshal.GetLastWin32Error()}");
            return;
        }
        _rawInputRegistered = true;
        _windowSource = HwndSource.FromHwnd(windowHandle);
        _windowSource?.AddHook(RawInputWindowProc);
    }

    private IntPtr RawInputWindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmInput) return IntPtr.Zero;
        var headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        uint size = 0;
        if (GetRawInputData(lParam, RidInput, IntPtr.Zero, ref size, headerSize) == uint.MaxValue || size == 0)
            return IntPtr.Zero;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RidInput, buffer, ref size, headerSize) == uint.MaxValue)
                return IntPtr.Zero;
            var header = Marshal.PtrToStructure<RawInputHeader>(buffer);
            if (header.Type != RimTypeKeyboard) return IntPtr.Zero;
            var keyboard = Marshal.PtrToStructure<RawKeyboard>(IntPtr.Add(buffer, (int)headerSize));
            var isDown = (keyboard.Flags & RiKeyBreak) == 0;
            var isRightAlt = keyboard.VirtualKey == VkRmenu
                || (keyboard.VirtualKey == VkMenu && (keyboard.Flags & RiKeyE0) != 0);
            if (isRightAlt) UpdateRightAltState(isDown);
            else if (keyboard.VirtualKey == VkPrior) UpdatePageUpState(isDown);
            else if (keyboard.VirtualKey == VkNext) UpdatePageDownState(isDown);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return IntPtr.Zero;
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var virtualKey = Marshal.ReadInt32(lParam);
            var message = wParam.ToInt32();
            var keyDown = message == WmKeydown || message == WmSyskeydown;
            var keyUp = message == WmKeyup || message == WmSyskeyup;
            if (virtualKey == VkRmenu && keyDown)
            {
                UpdateRightAltState(true);
            }
            else if (virtualKey == VkRmenu && keyUp)
            {
                UpdateRightAltState(false);
            }
            else if (virtualKey == VkPrior)
            {
                if (keyDown) UpdatePageUpState(true);
                else if (keyUp) UpdatePageUpState(false);
                if (PageUpPressed is not null) return new IntPtr(1);
            }
            else if (virtualKey == VkNext)
            {
                if (keyDown) UpdatePageDownState(true);
                else if (keyUp) UpdatePageDownState(false);
                if (PageDownPressed is not null) return new IntPtr(1);
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private void PollGlobalKeyStates()
    {
        UpdateRightAltState(IsKeyDown(VkRmenu));
        UpdatePageUpState(IsKeyDown(VkPrior));
        UpdatePageDownState(IsKeyDown(VkNext));
    }

    private void UpdateRightAltState(bool isDown)
    {
        EventHandler? handler;
        lock (_stateGate)
        {
            if (_isDown == isDown) return;
            _isDown = isDown;
            handler = isDown ? Pressed : Released;
        }
        VoiceScreenLog.Info(isDown ? "Hotkey RightAlt pressed" : "Hotkey RightAlt released");
        Raise(handler);
    }

    private void UpdatePageUpState(bool isDown)
    {
        EventHandler? handler = null;
        lock (_stateGate)
        {
            if (_pageUpDown == isDown) return;
            _pageUpDown = isDown;
            if (isDown) handler = PageUpPressed;
        }
        Raise(handler);
    }

    private void UpdatePageDownState(bool isDown)
    {
        EventHandler? handler = null;
        lock (_stateGate)
        {
            if (_pageDownDown == isDown) return;
            _pageDownDown = isDown;
            if (isDown) handler = PageDownPressed;
        }
        Raise(handler);
    }

    private void Raise(EventHandler? handler)
    {
        if (handler is null) return;
        var context = _eventContext;
        if (context is null) handler.Invoke(this, EventArgs.Empty);
        else context.Post(_ => handler.Invoke(this, EventArgs.Empty), null);
    }

    private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public void Dispose()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _windowSource?.RemoveHook(RawInputWindowProc);
        _windowSource = null;
        if (_rawInputRegistered)
        {
            var remove = new[]
            {
                new RawInputDevice { UsagePage = 0x01, Usage = 0x06, Flags = RidevRemove, Target = IntPtr.Zero }
            };
            RegisterRawInputDevices(remove, 1, (uint)Marshal.SizeOf<RawInputDevice>());
            _rawInputRegistered = false;
        }
        _eventContext = null;
        lock (_stateGate)
        {
            _isDown = false;
            _pageUpDown = false;
            _pageDownDown = false;
        }
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawKeyboard
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VirtualKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint deviceCount, uint size);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
