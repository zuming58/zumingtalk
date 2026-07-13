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
    private const int VK_CONTROL = 0x11;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_SPACE = 0x20;

    private readonly LowLevelKeyboardProc callback;
    private IntPtr hookId;
    private bool rightAltDown;
    private bool controlDown;
    private bool winDown;
    private bool fallbackChordDown;

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
        controlDown = false;
        winDown = false;
        fallbackChordDown = false;
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
        var isKeyDown = message is WM_KEYDOWN or WM_SYSKEYDOWN;
        var isKeyUp = message is WM_KEYUP or WM_SYSKEYUP;

        UpdateModifierState(vkCode, isKeyDown, isKeyUp);

        if (vkCode == VK_RMENU)
        {
            if (isKeyDown)
            {
                rightAltDown = true;
                return new IntPtr(1);
            }

            if (rightAltDown && isKeyUp)
            {
                rightAltDown = false;
                HotkeyPressed?.Invoke(this, new HotkeyPressedEventArgs(HotkeyAction.ToggleDictation));
                return new IntPtr(1);
            }
        }

        if (IsFallbackToggle(vkCode, isKeyDown, isKeyUp))
        {
            return new IntPtr(1);
        }

        if (vkCode == VK_ESCAPE && isKeyDown)
        {
            HotkeyPressed?.Invoke(this, new HotkeyPressedEventArgs(HotkeyAction.CancelDictation));
        }

        return CallNextHookEx(hookId, nCode, wParam, lParam);
    }

    internal bool ApplyKeyForTest(int vkCode, bool isKeyDown)
    {
        UpdateModifierState(vkCode, isKeyDown, !isKeyDown);
        return IsFallbackToggle(vkCode, isKeyDown, !isKeyDown);
    }

    private void UpdateModifierState(int vkCode, bool isKeyDown, bool isKeyUp)
    {
        if (vkCode is VK_CONTROL or VK_LCONTROL or VK_RCONTROL)
        {
            controlDown = isKeyDown || (controlDown && !isKeyUp);
        }

        if (vkCode is VK_LWIN or VK_RWIN)
        {
            winDown = isKeyDown || (winDown && !isKeyUp);
        }
    }

    private bool IsFallbackToggle(int vkCode, bool isKeyDown, bool isKeyUp)
    {
        if (vkCode == VK_SPACE && isKeyDown && controlDown && winDown && !fallbackChordDown)
        {
            fallbackChordDown = true;
            HotkeyPressed?.Invoke(this, new HotkeyPressedEventArgs(HotkeyAction.ToggleDictation));
            return true;
        }

        if ((vkCode == VK_SPACE && isKeyUp) || !controlDown || !winDown)
        {
            fallbackChordDown = false;
        }

        return false;
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
