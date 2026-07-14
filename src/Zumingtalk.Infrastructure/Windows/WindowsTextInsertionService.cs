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

        if (target.Kind == InputTargetKind.Lost || target.FocusHandle == IntPtr.Zero || !IsWindow(target.FocusHandle))
        {
            return Task.FromResult(CreateLostTargetResult());
        }

        if (target.Kind != InputTargetKind.Editable)
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
            SetClipboardTextWithRetry(text);
            return Task.FromResult(new TextInsertionResult(false, TextInsertionMethod.CopyFallback, "Target is elevated; text copied for manual paste."));
        }

        if (RequiresKeyboardPaste(target.ClassName))
        {
            var sentEvents = TryKeyboardClipboardPaste(text);
            return Task.FromResult(EvaluateKeyboardPasteAttempt(sentEvents));
        }

        var pasted = TryClipboardPaste(target.FocusHandle, text, useWindowMessage: true);
        if (pasted.Verified)
        {
            return Task.FromResult(new TextInsertionResult(true, TextInsertionMethod.PasteMessage, pasted.Message));
        }

        if (pasted.KeepClipboardFallback)
        {
            return Task.FromResult(new TextInsertionResult(false, TextInsertionMethod.CopyFallback, pasted.Message));
        }

        var sent = TryClipboardPaste(target.FocusHandle, text, useWindowMessage: false);
        if (sent.Verified)
        {
            return Task.FromResult(new TextInsertionResult(true, TextInsertionMethod.SendInputPaste, sent.Message));
        }

        if (sent.KeepClipboardFallback)
        {
            return Task.FromResult(new TextInsertionResult(false, TextInsertionMethod.CopyFallback, sent.Message));
        }

        SetClipboardTextWithRetry(text);
        return Task.FromResult(new TextInsertionResult(false, TextInsertionMethod.CopyFallback, "Automatic insertion was not verified; text copied."));
    }

    public Task<TextInsertionResult> CopyOnlyAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetClipboardTextWithRetry(text);
        return Task.FromResult(new TextInsertionResult(false, TextInsertionMethod.CopyOnly, "Copy-only mode; text copied."));
    }

    public CapturedInputTarget ValidateCapturedTarget(CapturedInputTarget capturedTarget)
    {
        if (capturedTarget.Kind != InputTargetKind.Editable ||
            capturedTarget.WindowHandle == IntPtr.Zero ||
            capturedTarget.FocusHandle == IntPtr.Zero ||
            !IsWindow(capturedTarget.WindowHandle) ||
            !IsWindow(capturedTarget.FocusHandle))
        {
            return capturedTarget with { Kind = InputTargetKind.Lost };
        }

        var foreground = GetForegroundWindow();
        if (foreground != capturedTarget.WindowHandle)
        {
            return capturedTarget with { Kind = InputTargetKind.Lost };
        }

        var threadId = GetWindowThreadProcessId(foreground, out var processId);
        if ((int)processId != capturedTarget.ProcessId)
        {
            return capturedTarget with { Kind = InputTargetKind.Lost };
        }

        var info = new GUITHREADINFO { cbSize = GUITHREADINFO_SIZE };
        if (!GetGUIThreadInfo(threadId, ref info) || info.hwndFocus != capturedTarget.FocusHandle)
        {
            return capturedTarget with { Kind = InputTargetKind.Lost };
        }

        return capturedTarget;
    }

    public static TextInsertionResult CreateLostTargetResult() =>
        new(false, TextInsertionMethod.Auto, "Captured target was lost; history only.");

    internal static PasteAttemptResult EvaluatePasteAttempt(int beforeLength, int afterLength, string methodName)
    {
        if (beforeLength < 0 || afterLength < 0)
        {
            return new PasteAttemptResult(false, true, $"{methodName} could not be verified; text kept on clipboard.");
        }

        return afterLength > beforeLength
            ? new PasteAttemptResult(true, false, $"{methodName} was verified.")
            : new PasteAttemptResult(false, false, $"{methodName} did not change target text.");
    }

    internal const int ExpectedCtrlVEventCount = 4;

    internal static TextInsertionResult EvaluateKeyboardPasteAttempt(uint sentEvents) =>
        sentEvents == ExpectedCtrlVEventCount
            ? new TextInsertionResult(false, TextInsertionMethod.SendInputPaste, "Keyboard paste was attempted; text kept on clipboard as fallback.")
            : new TextInsertionResult(false, TextInsertionMethod.CopyFallback, "Keyboard paste was blocked; text kept on clipboard.");

    private static uint TryKeyboardClipboardPaste(string text)
    {
        SetClipboardTextWithRetry(text);
        Thread.Sleep(80);
        return SendCtrlV();
    }

    private static PasteAttemptResult TryClipboardPaste(IntPtr focusHandle, string text, bool useWindowMessage)
    {
        var previousText = TryGetClipboardText();
        SetClipboardTextWithRetry(text);
        var before = GetTextLength(focusHandle);
        var methodName = useWindowMessage ? "WM_PASTE" : "SendInput Ctrl+V";

        if (useWindowMessage)
        {
            _ = SendMessage(focusHandle, WM_PASTE, IntPtr.Zero, string.Empty);
        }
        else
        {
            _ = SendCtrlV();
        }

        Thread.Sleep(120);
        var after = GetTextLength(focusHandle);
        var result = EvaluatePasteAttempt(before, after, methodName);

        if (!result.KeepClipboardFallback && previousText is not null)
        {
            SetClipboardTextWithRetry(previousText);
        }

        return result;
    }

    internal sealed record PasteAttemptResult(bool Verified, bool KeepClipboardFallback, string Message);

    private static CapturedInputTarget None(string processName) =>
        new(InputTargetKind.None, IntPtr.Zero, IntPtr.Zero, 0, processName, GetCurrentIntegrityLevel());

    private static bool IsEditableClass(string className) =>
        IsNativeEditClass(className) ||
        className.Contains("Chrome", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("WebView", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("Internet Explorer_Server", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("HwndWrapper", StringComparison.OrdinalIgnoreCase);

    internal static bool RequiresKeyboardPaste(string className) =>
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

    private static void SetClipboardTextWithRetry(string text)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch (Exception ex) when (ex is ExternalException or ThreadStateException)
            {
                lastError = ex;
                Thread.Sleep(50 * (attempt + 1));
            }
        }

        throw new InvalidOperationException("Clipboard is busy; text could not be copied.", lastError);
    }

    private static uint SendCtrlV()
    {
        var inputs = new[]
        {
            KeyboardInput(VK_CONTROL, 0),
            KeyboardInput(VK_V, 0),
            KeyboardInput(VK_V, KEYEVENTF_KEYUP),
            KeyboardInput(VK_CONTROL, KEYEVENTF_KEYUP)
        };

        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
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
