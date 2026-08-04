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
    private const int VkLmenu = 0xA4;
    private const int VkPrior = 0x21; // Page Up
    private const int VkNext = 0x22; // Page Down
    private const int VkMenu = 0x12;
    private const int WmInput = 0x00FF;
    private const int WmHotkey = 0x0312;
    private const int RightAltHotkeyId = 0x5653;
    private const uint ModNorepeat = 0x4000;
    private const uint LlkhfExtended = 0x00000001;
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
    private Timer? _releaseTimer;
    private SynchronizationContext? _eventContext;
    private HwndSource? _windowSource;
    private bool _rawInputRegistered;
    private bool _registeredHotkey;
    private IntPtr _windowHandle;
    private bool _isDown;
    private bool _rawRightAltDown;
    private bool _hookRightAltDown;
    private bool _polledRightAltDown;
    private bool _registeredRightAltDown;
    private bool _releasePending;
    private long _registeredRightAltTimestamp;
    private string _lastRightAltSource = "unknown";
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
            VoiceScreenLog.Info("Global hotkeys started: RegisterHotKey + raw input sink + low-level hook + async key polling fallback");
    }

    private void RegisterRawKeyboard(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            VoiceScreenLog.Warn("Raw keyboard input sink unavailable: main window handle is empty");
            return;
        }
        _windowHandle = windowHandle;
        _windowSource = HwndSource.FromHwnd(windowHandle);
        _windowSource?.AddHook(RawInputWindowProc);
        _registeredHotkey = RegisterHotKey(windowHandle, RightAltHotkeyId, ModNorepeat, VkRmenu);
        if (!_registeredHotkey)
            VoiceScreenLog.Warn($"RegisterHotKey RightAlt unavailable; other three channels remain active. win32={Marshal.GetLastWin32Error()}");
        else
            VoiceScreenLog.Info("RegisterHotKey RightAlt registered successfully");

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
    }

    private IntPtr RawInputWindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == RightAltHotkeyId)
        {
            UpdateRightAltState(true, "RegisterHotKey");
            return IntPtr.Zero;
        }
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
                || (keyboard.VirtualKey == VkMenu && (keyboard.Flags & RiKeyE0) != 0)
                || (keyboard.MakeCode == 0x38 && (keyboard.Flags & RiKeyE0) != 0);
            if (isRightAlt) UpdateRightAltState(isDown, "RawInput-E0");
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
            var keyboard = Marshal.PtrToStructure<LowLevelKeyboardInput>(lParam);
            var virtualKey = (int)keyboard.VirtualKey;
            var message = wParam.ToInt32();
            var keyDown = message == WmKeydown || message == WmSyskeydown;
            var keyUp = message == WmKeyup || message == WmSyskeyup;
            var isRightAlt = virtualKey == VkRmenu
                || (virtualKey == VkMenu && (keyboard.Flags & LlkhfExtended) != 0)
                || (keyboard.ScanCode == 0x38 && (keyboard.Flags & LlkhfExtended) != 0);
            if (isRightAlt && keyDown)
            {
                UpdateRightAltState(true, "LowLevelHook-E0");
            }
            else if (isRightAlt && keyUp)
            {
                UpdateRightAltState(false, "LowLevelHook-E0");
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
        // 某些游戏/键盘布局把右 Alt 只暴露成通用 VK_MENU。只在左 Alt 未按下时采用该兜底，
        // 避免把普通左 Alt 误当成发送键。
        var rightAltDown = IsKeyDown(VkRmenu) || (IsKeyDown(VkMenu) && !IsKeyDown(VkLmenu));
        UpdateRightAltState(rightAltDown, "AsyncKeyState");
        UpdatePageUpState(IsKeyDown(VkPrior));
        UpdatePageDownState(IsKeyDown(VkNext));
    }

    private void UpdateRightAltState(bool isDown, string source)
    {
        EventHandler? handler = null;
        string? logMessage = null;
        lock (_stateGate)
        {
            var channelChanged = source switch
            {
                "RawInput-E0" => _rawRightAltDown != isDown,
                "LowLevelHook-E0" => _hookRightAltDown != isDown,
                "AsyncKeyState" => _polledRightAltDown != isDown,
                "RegisterHotKey" => isDown && !_registeredRightAltDown,
                _ => false
            };
            switch (source)
            {
                case "RawInput-E0":
                    _rawRightAltDown = isDown;
                    break;
                case "LowLevelHook-E0":
                    _hookRightAltDown = isDown;
                    break;
                case "AsyncKeyState":
                    _polledRightAltDown = isDown;
                    if (!isDown && _registeredRightAltDown
                        && Stopwatch.GetElapsedTime(_registeredRightAltTimestamp) >= TimeSpan.FromMilliseconds(60))
                        _registeredRightAltDown = false;
                    break;
                case "RegisterHotKey":
                    if (isDown)
                    {
                        _registeredRightAltDown = true;
                        _registeredRightAltTimestamp = Stopwatch.GetTimestamp();
                    }
                    break;
            }

            if (channelChanged)
                VoiceScreenLog.Info($"Hotkey channel {source}={(isDown ? "down" : "up")}");

            if (isDown) _lastRightAltSource = source;
            if (IsAnyRightAltChannelDown())
            {
                _releaseTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                _releasePending = false;
                if (!_isDown)
                {
                    _isDown = true;
                    handler = Pressed;
                    logMessage = $"Hotkey RightAlt pressed source={source} foreground={DescribeForegroundProcess()}";
                }
            }
            else if (_isDown && !_releasePending)
            {
                // 三条输入通道的消息到达顺序并不固定。短暂等待后再次聚合，避免一次长按
                // 被轮询中的瞬时 false 切成“释放→再次按下”。
                _releaseTimer ??= new Timer(_ => CommitDebouncedRelease(), null,
                    Timeout.Infinite, Timeout.Infinite);
                _releasePending = true;
                _releaseTimer.Change(35, Timeout.Infinite);
            }
        }
        if (logMessage is not null) VoiceScreenLog.Info(logMessage);
        Raise(handler);
    }

    private bool IsAnyRightAltChannelDown()
        => _rawRightAltDown || _hookRightAltDown || _polledRightAltDown || _registeredRightAltDown;

    private void CommitDebouncedRelease()
    {
        EventHandler? handler = null;
        string? logMessage = null;
        lock (_stateGate)
        {
            _releasePending = false;
            if (!_polledRightAltDown && _registeredRightAltDown
                && Stopwatch.GetElapsedTime(_registeredRightAltTimestamp) >= TimeSpan.FromMilliseconds(60))
                _registeredRightAltDown = false;
            if (!_isDown || IsAnyRightAltChannelDown()) return;
            _isDown = false;
            handler = Released;
            logMessage = $"Hotkey RightAlt released source=Aggregated({_lastRightAltSource}) foreground={DescribeForegroundProcess()}";
        }
        VoiceScreenLog.Info(logMessage);
        Raise(handler);
    }

    private static string DescribeForegroundProcess()
    {
        try
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero) return "none";
            GetWindowThreadProcessId(window, out var processId);
            using var process = Process.GetProcessById((int)processId);
            return $"{process.ProcessName}({processId})";
        }
        catch
        {
            return "unavailable";
        }
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
        _releaseTimer?.Dispose();
        _releaseTimer = null;
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _windowSource?.RemoveHook(RawInputWindowProc);
        _windowSource = null;
        if (_registeredHotkey && _windowHandle != IntPtr.Zero)
        {
            UnregisterHotKey(_windowHandle, RightAltHotkeyId);
            _registeredHotkey = false;
        }
        _windowHandle = IntPtr.Zero;
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
            _rawRightAltDown = false;
            _hookRightAltDown = false;
            _polledRightAltDown = false;
            _registeredRightAltDown = false;
            _releasePending = false;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelKeyboardInput
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInformation;
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
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, int virtualKey);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
