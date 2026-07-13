using System.Diagnostics;
using System.Runtime.InteropServices;
using Zumingtalk.Domain.Services;

namespace Zumingtalk.Infrastructure.Windows;

public sealed class GlobalHotkeyService : IGlobalHotkeyService, IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int VK_RMENU = 0xA5;
    private const int VK_ESCAPE = 0x1B;

    private readonly LowLevelKeyboardProc callback;
    private IntPtr hookId;
    private bool rightAltDown;

    public GlobalHotkeyService()
    {
        callback = HookCallback;
    }

    public event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;

    public void Start()
    {
        if (hookId != IntPtr.Zero)
        {
            return;
        }

        hookId = SetHook(callback);
    }

    public void Stop()
    {
        if (hookId == IntPtr.Zero)
        {
            return;
        }

        UnhookWindowsHookEx(hookId);
        hookId = IntPtr.Zero;
        rightAltDown = false;
    }

    public void Dispose()
    {
        Stop();
    }

    private static IntPtr SetHook(LowLevelKeyboardProc proc)
    {
        using var currentProcess = Process.GetCurrentProcess();
        using var currentModule = currentProcess.MainModule;
        return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(currentModule?.ModuleName), 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return CallNextHookEx(hookId, nCode, wParam, lParam);
        }

        var message = wParam.ToInt32();
        var vkCode = Marshal.ReadInt32(lParam);

        if (vkCode == VK_RMENU)
        {
            if (message is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                rightAltDown = true;
                return new IntPtr(1);
            }

            if (rightAltDown && (message is WM_KEYUP or WM_SYSKEYUP))
            {
                rightAltDown = false;
                HotkeyPressed?.Invoke(this, new HotkeyPressedEventArgs(HotkeyAction.ToggleDictation));
                return new IntPtr(1);
            }
        }

        if (vkCode == VK_ESCAPE && message == WM_KEYDOWN)
        {
            HotkeyPressed?.Invoke(this, new HotkeyPressedEventArgs(HotkeyAction.CancelDictation));
        }

        return CallNextHookEx(hookId, nCode, wParam, lParam);
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
