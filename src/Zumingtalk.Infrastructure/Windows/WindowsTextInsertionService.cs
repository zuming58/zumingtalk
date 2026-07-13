using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Zumingtalk.Domain.Dictation;
using Zumingtalk.Domain.Services;

namespace Zumingtalk.Infrastructure.Windows;

public sealed class WindowsTextInsertionService : ITextInsertionService
{
    private const int GUITHREADINFO_SIZE = 72;
    private const uint WM_GETTEXTLENGTH = 0x000E;
    private const uint EM_REPLACESEL = 0x00C2;
    private const uint WM_PASTE = 0x0302;
    private const int INPUT_KEYBOARD = 1;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    public CapturedInputTarget CaptureCurrentTarget()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return None("NoForegroundWindow");
        }

        var threadId = GetWindowThreadProcessId(foreground, out var processId);
        var info = new GUITHREADINFO { cbSize = GUITHREADINFO_SIZE };
        var focus = GetGUIThreadInfo(threadId, ref info) ? info.hwndFocus : IntPtr.Zero;
        var targetHandle = focus != IntPtr.Zero ? focus : foreground;
        var processName = GetProcessName((int)processId);
        var className = GetClassName(targetHandle);
        var kind = IsEditableClass(className) || HasTextCapacity(targetHandle)
            ? InputTargetKind.Editable
            : InputTargetKind.None;

        return new CapturedInputTarget(
            kind,
            foreground,
            targetHandle,
            (int)processId,
            processName,
            GetCurrentIntegrityLevel(),
            className,
            IsProcessElevated((int)processId));
    }

    public Task<TextInsertionResult> InsertAsync(CapturedInputTarget target, string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (target.Kind != InputTargetKind.Editable || target.FocusHandle == IntPtr.Zero || !IsWindow(target.FocusHandle))
        {
            return Task.FromResult(new TextInsertionResult(false, TextInsertionMethod.CopyOnly, "No editable target was captured."));
        }

        if (IsNativeEditClass(target.ClassName))
        {
            var nativeResult = SendMessage(target.FocusHandle, EM_REPLACESEL, new IntPtr(1), text);
            return Task.FromResult(new TextInsertionResult(true, TextInsertionMethod.NativeReplaceSelection, "Inserted with EM_REPLACESEL."));
        }

        if (target.IsElevated)
        {
            Clipboard.SetText(text);
            return Task.FromResult(new TextInsertionResult(false, TextInsertionMethod.CopyFallback, "Target is elevated; text copied for manual paste."));
        }

        var pasted = TryClipboardPaste(target.FocusHandle, text, useWindowMessage: true, out var pasteMessage);
        if (pasted)
        {
            return Task.FromResult(new TextInsertionResult(true, TextInsertionMethod.PasteMessage, pasteMessage));
        }

        var sent = TryClipboardPaste(target.FocusHandle, text, useWindowMessage: false, out var sendInputMessage);
        if (sent)
        {
            return Task.FromResult(new TextInsertionResult(true, TextInsertionMethod.SendInputPaste, sendInputMessage));
        }

        Clipboard.SetText(text);
        return Task.FromResult(new TextInsertionResult(false, TextInsertionMethod.CopyFallback, "Automatic insertion was not verified; text copied."));
    }

    private static bool TryClipboardPaste(IntPtr focusHandle, string text, bool useWindowMessage, out string message)
    {
        var previousText = TryGetClipboardText();
        Clipboard.SetText(text);
        var before = GetTextLength(focusHandle);

        if (useWindowMessage)
        {
            _ = SendMessage(focusHandle, WM_PASTE, IntPtr.Zero, string.Empty);
        }
        else
        {
            SendCtrlV();
        }

        Thread.Sleep(120);
        var after = GetTextLength(focusHandle);
        var verified = after < 0 || before < 0 || after >= before + Math.Min(text.Length, 1);

        if (verified && previousText is not null)
        {
            Clipboard.SetText(previousText);
        }

        message = verified ? "Paste command was sent." : "Paste command could not be verified.";
        return verified;
    }

    private static CapturedInputTarget None(string processName) =>
        new(InputTargetKind.None, IntPtr.Zero, IntPtr.Zero, 0, processName, GetCurrentIntegrityLevel());

    private static bool IsEditableClass(string className) =>
        IsNativeEditClass(className) ||
        className.Contains("Chrome", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("WebView", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("Internet Explorer_Server", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("HwndWrapper", StringComparison.OrdinalIgnoreCase);

    private static bool IsNativeEditClass(string className) =>
        className.Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("RichEdit", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("RICHEDIT", StringComparison.OrdinalIgnoreCase);

    private static bool HasTextCapacity(IntPtr handle)
    {
        var length = GetTextLength(handle);
        return length >= 0 && IsNativeEditClass(GetClassName(handle));
    }

    private static int GetTextLength(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !IsWindow(handle))
        {
            return -1;
        }

        return (int)SendMessage(handle, WM_GETTEXTLENGTH, IntPtr.Zero, string.Empty);
    }

    private static string? TryGetClipboardText()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch
        {
            return null;
        }
    }

    private static void SendCtrlV()
    {
        var inputs = new[]
        {
            KeyboardInput(VK_CONTROL, 0),
            KeyboardInput(VK_V, 0),
            KeyboardInput(VK_V, KEYEVENTF_KEYUP),
            KeyboardInput(VK_CONTROL, KEYEVENTF_KEYUP)
        };

        _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT KeyboardInput(ushort key, uint flags) =>
        new()
        {
            type = INPUT_KEYBOARD,
            union = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = key,
                    dwFlags = flags
                }
            }
        };

    private static string GetClassName(IntPtr handle)
    {
        var builder = new StringBuilder(256);
        _ = GetClassName(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string GetProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch
        {
            return "Unknown";
        }
    }

    private static string GetCurrentIntegrityLevel() => "Medium";

    private static bool IsProcessElevated(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!OpenProcessToken(process.Handle, 0x0008, out var token))
            {
                return false;
            }

            try
            {
                var elevation = new TOKEN_ELEVATION();
                var size = Marshal.SizeOf<TOKEN_ELEVATION>();
                return GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenElevation, ref elevation, size, out _) &&
                    elevation.TokenIsElevated != 0;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        catch
        {
            return true;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, string lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        TOKEN_INFORMATION_CLASS tokenInformationClass,
        ref TOKEN_ELEVATION tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private enum TOKEN_INFORMATION_CLASS
    {
        TokenElevation = 20
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_ELEVATION
    {
        public int TokenIsElevated;
    }
}
