using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VoiceScreen.App.Input;

public sealed class RightAltHoldHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100;
    private const int WmKeyup = 0x0101;
    private const int WmSyskeydown = 0x0104;
    private const int WmSyskeyup = 0x0105;
    private const int VkRmenu = 0xA5;
    private readonly LowLevelKeyboardProc _callback;
    private IntPtr _hook;
    private bool _isDown;

    public RightAltHoldHook()
    {
        _callback = HookCallback;
    }

    public event EventHandler? Pressed;
    public event EventHandler? Released;

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule!;
        _hook = SetWindowsHookEx(WhKeyboardLl, _callback, GetModuleHandle(module.ModuleName), 0);
        if (_hook == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && Marshal.ReadInt32(lParam) == VkRmenu)
        {
            var message = wParam.ToInt32();
            if ((message == WmKeydown || message == WmSyskeydown) && !_isDown)
            {
                _isDown = true;
                Pressed?.Invoke(this, EventArgs.Empty);
            }
            else if ((message == WmKeyup || message == WmSyskeyup) && _isDown)
            {
                _isDown = false;
                Released?.Invoke(this, EventArgs.Empty);
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
