using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Zumingtalk.Domain.Services;

namespace Zumingtalk.Infrastructure.Windows;

public sealed class GlobalHotkeyService : IGlobalHotkeyService, IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WM_HOTKEY = 0x0312;
    private const int VK_RMENU = 0xA5;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_SPACE = 0x20;
    private const int FALLBACK_HOTKEY_ID = 0x5A10;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    private readonly LowLevelKeyboardProc callback;
    private readonly HotkeyMessageWindow messageWindow = new();
    private IntPtr hookId;
    private bool rightAltDown;
    private bool fallbackHotkeyEnabled = true;
    private bool fallbackHotkeyRegistered;

    public GlobalHotkeyService()
    {
        callback = HookCallback;
        messageWindow.HotkeyPressed += (_, _) => HotkeyPressed?.Invoke(this, new HotkeyPressedEventArgs(HotkeyAction.ToggleDictation));
    }

    public event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;

    public void Start()
    {
        if (hookId != IntPtr.Zero)
        {
            return;
        }

        hookId = SetHook(callback);
        SyncFallbackHotkeyRegistration();
    }

    public void SetFallbackHotkeyEnabled(bool enabled)
    {
        fallbackHotkeyEnabled = enabled;
        SyncFallbackHotkeyRegistration();
    }

    public void Stop()
    {
        if (hookId == IntPtr.Zero)
        {
            return;
        }

        UnhookWindowsHookEx(hookId);
        if (fallbackHotkeyRegistered)
        {
            UnregisterHotKey(messageWindow.Handle, FALLBACK_HOTKEY_ID);
            fallbackHotkeyRegistered = false;
        }

        hookId = IntPtr.Zero;
        rightAltDown = false;
    }

    public void Dispose()
    {
        Stop();
        messageWindow.Dispose();
    }

    private static IntPtr SetHook(LowLevelKeyboardProc proc)
    {
        using var currentProcess = Process.GetCurrentProcess();
        using var currentModule = currentProcess.MainModule;
        return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(currentModule?.ModuleName), 0);
    }

    private void SyncFallbackHotkeyRegistration()
    {
        if (hookId == IntPtr.Zero)
        {
            return;
        }

        if (fallbackHotkeyEnabled && !fallbackHotkeyRegistered)
        {
            fallbackHotkeyRegistered = RegisterHotKey(messageWindow.Handle, FALLBACK_HOTKEY_ID, MOD_CONTROL | MOD_WIN | MOD_NOREPEAT, VK_SPACE);
        }
        else if (!fallbackHotkeyEnabled && fallbackHotkeyRegistered)
        {
            UnregisterHotKey(messageWindow.Handle, FALLBACK_HOTKEY_ID);
            fallbackHotkeyRegistered = false;
        }
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

        if (vkCode == VK_ESCAPE && isKeyDown)
        {
            HotkeyPressed?.Invoke(this, new HotkeyPressedEventArgs(HotkeyAction.CancelDictation));
        }

        return CallNextHookEx(hookId, nCode, wParam, lParam);
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private sealed class HotkeyMessageWindow : NativeWindow, IDisposable
    {
        public HotkeyMessageWindow()
        {
            CreateHandle(new CreateParams());
        }

        public event EventHandler? HotkeyPressed;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == FALLBACK_HOTKEY_ID)
            {
                HotkeyPressed?.Invoke(this, EventArgs.Empty);
                return;
            }

            base.WndProc(ref m);
        }

        public void Dispose()
        {
            DestroyHandle();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
