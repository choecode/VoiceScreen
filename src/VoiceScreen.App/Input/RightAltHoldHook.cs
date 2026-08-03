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
    private const int VkPrior = 0x21; // Page Up
    private const int VkNext = 0x22; // Page Down
    private readonly LowLevelKeyboardProc _callback;
    private IntPtr _hook;
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
        if (code >= 0)
        {
            var virtualKey = Marshal.ReadInt32(lParam);
            var message = wParam.ToInt32();
            var keyDown = message == WmKeydown || message == WmSyskeydown;
            var keyUp = message == WmKeyup || message == WmSyskeyup;
            if (virtualKey == VkRmenu && keyDown && !_isDown)
            {
                _isDown = true;
                Pressed?.Invoke(this, EventArgs.Empty);
            }
            else if (virtualKey == VkRmenu && keyUp && _isDown)
            {
                _isDown = false;
                Released?.Invoke(this, EventArgs.Empty);
            }
            else if (virtualKey == VkPrior)
            {
                if (keyDown && !_pageUpDown)
                {
                    _pageUpDown = true;
                    PageUpPressed?.Invoke(this, EventArgs.Empty);
                }
                else if (keyUp) _pageUpDown = false;
                if (PageUpPressed is not null) return new IntPtr(1);
            }
            else if (virtualKey == VkNext)
            {
                if (keyDown && !_pageDownDown)
                {
                    _pageDownDown = true;
                    PageDownPressed?.Invoke(this, EventArgs.Empty);
                }
                else if (keyUp) _pageDownDown = false;
                if (PageDownPressed is not null) return new IntPtr(1);
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _isDown = false;
        _pageUpDown = false;
        _pageDownDown = false;
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
