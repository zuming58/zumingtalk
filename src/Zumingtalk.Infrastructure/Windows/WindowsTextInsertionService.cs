using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.Windows.Automation;
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
    private const int VK_MENU = 0x12;
    private const int VK_RMENU = 0xA5;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private static readonly int[] BlockingPasteModifiers =
    [
        VK_MENU,
        VK_RMENU,
        VK_LCONTROL,
        VK_RCONTROL,
        VK_LWIN,
        VK_RWIN
    ];

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
        var foregroundClassName = GetClassName(foreground);
        var className = GetClassName(targetHandle);
        var automationDiagnostics = CaptureAutomationDiagnostics(foregroundClassName, className, info.hwndCaret, (int)processId);
        var kind = IsSafeEditableTarget(processName, className, info.hwndCaret != IntPtr.Zero, automationDiagnostics.IsEditableCandidate)
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
            IsProcessElevated((int)processId),
            automationDiagnostics);
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
            var diagnostics = target.Diagnostics is null ? null : target.Diagnostics with { Strategy = "EM_REPLACESEL" };
            return Task.FromResult(new TextInsertionResult(true, TextInsertionMethod.NativeReplaceSelection, "Inserted with EM_REPLACESEL.", diagnostics));
        }

        if (target.IsElevated)
        {
            SetClipboardTextWithRetry(text);
            var diagnostics = target.Diagnostics is null ? null : target.Diagnostics with { Strategy = "ElevatedCopyFallback", KeepsClipboardFallback = true };
            return Task.FromResult(new TextInsertionResult(false, TextInsertionMethod.CopyFallback, "Target is elevated; text copied for manual paste.", diagnostics));
        }

        if (RequiresKeyboardPaste(target))
        {
            var sentEvents = TryKeyboardClipboardPaste(target, text);
            return Task.FromResult(EvaluateKeyboardPasteAttempt(sentEvents, target.Diagnostics));
        }

        var pasted = TryClipboardPaste(target, text, useWindowMessage: true);
        if (pasted.Verified)
        {
            var diagnostics = target.Diagnostics is null ? null : target.Diagnostics with { Strategy = "WM_PASTE", KeepsClipboardFallback = pasted.KeepClipboardFallback };
            return Task.FromResult(new TextInsertionResult(true, TextInsertionMethod.PasteMessage, pasted.Message, diagnostics));
        }

        if (pasted.KeepClipboardFallback)
        {
            var diagnostics = target.Diagnostics is null ? null : target.Diagnostics with { Strategy = "WM_PASTE", KeepsClipboardFallback = true };
            return Task.FromResult(new TextInsertionResult(false, TextInsertionMethod.CopyFallback, pasted.Message, diagnostics));
        }

        var sent = TryClipboardPaste(target, text, useWindowMessage: false);
        if (sent.Verified)
        {
            var diagnostics = target.Diagnostics is null ? null : target.Diagnostics with { Strategy = "SendInput", SendInputEvents = sent.SendInputEvents, LastWin32Error = sent.LastWin32Error };
            return Task.FromResult(new TextInsertionResult(true, TextInsertionMethod.SendInputPaste, sent.Message, diagnostics));
        }

        if (sent.KeepClipboardFallback)
        {
            var diagnostics = target.Diagnostics is null ? null : target.Diagnostics with { Strategy = "SendInput", SendInputEvents = sent.SendInputEvents, LastWin32Error = sent.LastWin32Error, KeepsClipboardFallback = true };
            return Task.FromResult(new TextInsertionResult(false, TextInsertionMethod.CopyFallback, sent.Message, diagnostics));
        }

        SetClipboardTextWithRetry(text);
        var fallbackDiagnostics = target.Diagnostics is null ? null : target.Diagnostics with { Strategy = "CopyFallback", KeepsClipboardFallback = true };
        return Task.FromResult(new TextInsertionResult(false, TextInsertionMethod.CopyFallback, "Automatic insertion was not verified; text copied.", fallbackDiagnostics));
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
        if (!GetGUIThreadInfo(threadId, ref info))
        {
            return capturedTarget with { Kind = InputTargetKind.Lost };
        }

        var currentFocus = info.hwndFocus != IntPtr.Zero ? info.hwndFocus : foreground;
        if (currentFocus == IntPtr.Zero || !IsWindow(currentFocus))
        {
            return capturedTarget with { Kind = InputTargetKind.Lost };
        }

        var currentFocusClassName = GetClassName(currentFocus);
        var diagnostics = CaptureAutomationDiagnostics(GetClassName(foreground), currentFocusClassName, info.hwndCaret, capturedTarget.ProcessId);
        if (!IsSafeEditableTarget(capturedTarget.ProcessName, currentFocusClassName, info.hwndCaret != IntPtr.Zero, diagnostics.IsEditableCandidate))
        {
            return capturedTarget with { Kind = InputTargetKind.Lost, Diagnostics = diagnostics };
        }

        return capturedTarget with
        {
            Kind = InputTargetKind.Editable,
            FocusHandle = currentFocus,
            ClassName = currentFocusClassName,
            Diagnostics = diagnostics
        };
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

    internal static TextInsertionResult EvaluateKeyboardPasteAttempt(KeyboardPasteAttemptResult attempt, InputTargetDiagnostics? diagnostics = null)
    {
        var resultDiagnostics = diagnostics is null
            ? null
            : diagnostics with
            {
                Strategy = !attempt.TargetStillForeground
                    ? "TargetChangedCopyFallback"
                    : attempt.ModifiersReleased ? "SendInput" : "SendInputBlockedByModifier",
                SendInputEvents = attempt.SentEvents,
                LastWin32Error = attempt.LastWin32Error,
                KeepsClipboardFallback = !attempt.TextChangeVerified
            };

        return attempt.ModifiersReleased && attempt.TargetStillForeground && attempt.SentEvents == ExpectedCtrlVEventCount && attempt.TextChangeVerified
            ? new TextInsertionResult(true, TextInsertionMethod.SendInputPaste, "Keyboard paste was verified by UI Automation.", resultDiagnostics)
            : attempt.ModifiersReleased && attempt.TargetStillForeground && attempt.SentEvents == ExpectedCtrlVEventCount
            ? new TextInsertionResult(false, TextInsertionMethod.SendInputPaste, "Keyboard paste was attempted; text kept on clipboard as fallback.", resultDiagnostics)
            : new TextInsertionResult(false, TextInsertionMethod.CopyFallback,
                attempt.TargetStillForeground
                    ? "Keyboard paste was blocked; text kept on clipboard."
                    : "Target changed before keyboard paste; text kept on clipboard.",
                resultDiagnostics);
    }

    internal static TextInsertionResult EvaluateKeyboardPasteAttempt(uint sentEvents) =>
        sentEvents == ExpectedCtrlVEventCount
            ? new TextInsertionResult(false, TextInsertionMethod.SendInputPaste, "Keyboard paste was attempted; text kept on clipboard as fallback.")
            : new TextInsertionResult(false, TextInsertionMethod.CopyFallback, "Keyboard paste was blocked; text kept on clipboard.");

    private static KeyboardPasteAttemptResult TryKeyboardClipboardPaste(CapturedInputTarget target, string text)
    {
        var previousText = TryGetClipboardText();
        var beforeText = TryGetFocusedAutomationText(target.ProcessId);
        SetClipboardTextWithRetry(text);
        Thread.Sleep(80);
        if (!WaitForBlockingModifiersToClear())
        {
            return new KeyboardPasteAttemptResult(0, Marshal.GetLastWin32Error(), false);
        }

        if (!IsSameForegroundTarget(target))
        {
            return new KeyboardPasteAttemptResult(0, 0, true, false);
        }

        var attempt = SendCtrlV();
        if (attempt.SentEvents != ExpectedCtrlVEventCount)
        {
            return attempt;
        }

        Thread.Sleep(120);
        var afterText = TryGetFocusedAutomationText(target.ProcessId);
        var textChangeVerified = beforeText is not null && afterText is not null && !string.Equals(beforeText, afterText, StringComparison.Ordinal);
        if (textChangeVerified && previousText is not null)
        {
            SetClipboardTextWithRetry(previousText);
        }

        return attempt with { TextChangeVerified = textChangeVerified };
    }

    private static PasteAttemptResult TryClipboardPaste(CapturedInputTarget target, string text, bool useWindowMessage)
    {
        var previousText = TryGetClipboardText();
        SetClipboardTextWithRetry(text);
        var before = GetTextLength(target.FocusHandle);
        var methodName = useWindowMessage ? "WM_PASTE" : "SendInput Ctrl+V";
        var sendInput = new KeyboardPasteAttemptResult(0, 0, true);

        if (useWindowMessage)
        {
            _ = SendMessage(target.FocusHandle, WM_PASTE, IntPtr.Zero, string.Empty);
        }
        else
        {
            if (!WaitForBlockingModifiersToClear())
            {
                return new PasteAttemptResult(false, true, $"{methodName} was blocked while modifier keys were down.", 0, Marshal.GetLastWin32Error());
            }

            if (!IsSameForegroundTarget(target))
            {
                return new PasteAttemptResult(false, true, $"{methodName} was cancelled because the target changed.", 0, 0);
            }

            sendInput = SendCtrlV();
        }

        Thread.Sleep(120);
        var after = GetTextLength(target.FocusHandle);
        var result = EvaluatePasteAttempt(before, after, methodName);
        result = result with { SendInputEvents = sendInput.SentEvents, LastWin32Error = sendInput.LastWin32Error };

        if (!result.KeepClipboardFallback && previousText is not null)
        {
            SetClipboardTextWithRetry(previousText);
        }

        return result;
    }

    internal sealed record PasteAttemptResult(bool Verified, bool KeepClipboardFallback, string Message, uint SendInputEvents = 0, int LastWin32Error = 0);

    internal sealed record KeyboardPasteAttemptResult(
        uint SentEvents,
        int LastWin32Error,
        bool ModifiersReleased,
        bool TargetStillForeground = true,
        bool TextChangeVerified = false);

    private static CapturedInputTarget None(string processName) =>
        new(InputTargetKind.None, IntPtr.Zero, IntPtr.Zero, 0, processName, GetCurrentIntegrityLevel());

    internal static bool IsSafeEditableTarget(string className, bool hasCaret, bool automationCandidate) =>
        IsNativeEditClass(className) ||
        automationCandidate ||
        (hasCaret && RequiresKeyboardPaste(className));

    internal static bool IsSafeEditableTarget(string processName, string className, bool hasCaret, bool automationCandidate) =>
        IsSafeEditableTarget(className, hasCaret, automationCandidate) ||
        IsKnownQtKeyboardPasteTarget(processName, className);

    internal static bool IsKnownQtKeyboardPasteTarget(string processName, string className) =>
        className.StartsWith("Qt", StringComparison.OrdinalIgnoreCase) &&
        (processName.Equals("Weixin", StringComparison.OrdinalIgnoreCase) ||
         processName.Equals("WeChat", StringComparison.OrdinalIgnoreCase) ||
         processName.Equals("WXWork", StringComparison.OrdinalIgnoreCase));

    internal static bool RequiresKeyboardPaste(string className) =>
        className.Contains("Chrome", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("WebView", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("Internet Explorer_Server", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("HwndWrapper", StringComparison.OrdinalIgnoreCase);

    private static bool RequiresKeyboardPaste(CapturedInputTarget target) =>
        RequiresKeyboardPaste(target.ClassName) ||
        IsKnownQtKeyboardPasteTarget(target.ProcessName, target.ClassName) ||
        target.Diagnostics?.IsEditableCandidate == true;

    private static bool IsSameForegroundTarget(CapturedInputTarget target)
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground != target.WindowHandle)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(foreground, out var processId);
        return (int)processId == target.ProcessId;
    }

    private static InputTargetDiagnostics CaptureAutomationDiagnostics(
        string foregroundClassName,
        string focusClassName,
        IntPtr caretHandle,
        int processId)
    {
        var keyboardState = DescribeBlockingModifierState();
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is null)
            {
                return EmptyDiagnostics(foregroundClassName, focusClassName, caretHandle, keyboardState);
            }

            var automationProcessId = GetAutomationProcessId(focused);
            if (automationProcessId != 0 && automationProcessId != processId)
            {
                return EmptyDiagnostics(foregroundClassName, focusClassName, caretHandle, keyboardState);
            }

            var controlType = GetAutomationControlType(focused);
            var automationClassName = GetAutomationStringProperty(focused, AutomationElement.ClassNameProperty);
            var automationId = GetAutomationStringProperty(focused, AutomationElement.AutomationIdProperty);
            var isKeyboardFocusable = GetAutomationBoolProperty(focused, AutomationElement.IsKeyboardFocusableProperty);
            var isEnabled = GetAutomationBoolProperty(focused, AutomationElement.IsEnabledProperty, defaultValue: true);
            var supportsValuePattern = TryGetValuePattern(focused, out var valuePattern);
            var supportsTextPattern = TryGetPattern(focused, TextPattern.Pattern);
            var valuePatternReadonly = supportsValuePattern && valuePattern?.Current.IsReadOnly == true;
            var editableCandidate =
                isEnabled &&
                (isKeyboardFocusable || focusClassName.Contains("Chrome", StringComparison.OrdinalIgnoreCase) || focusClassName.Contains("WebView", StringComparison.OrdinalIgnoreCase)) &&
                ((controlType == ControlType.Edit.ProgrammaticName) ||
                 (supportsValuePattern && !valuePatternReadonly) ||
                 (supportsTextPattern && HasEditableAutomationHint(controlType, automationClassName, focusClassName)));

            return new InputTargetDiagnostics(
                foregroundClassName,
                focusClassName,
                caretHandle,
                controlType,
                automationClassName,
                automationId,
                isKeyboardFocusable,
                isEnabled,
                supportsValuePattern,
                supportsTextPattern,
                editableCandidate,
                keyboardState,
                "UIA");
        }
        catch
        {
            return EmptyDiagnostics(foregroundClassName, focusClassName, caretHandle, keyboardState);
        }
    }

    private static InputTargetDiagnostics EmptyDiagnostics(string foregroundClassName, string focusClassName, IntPtr caretHandle, string keyboardState) =>
        new(
            foregroundClassName,
            focusClassName,
            caretHandle,
            string.Empty,
            string.Empty,
            string.Empty,
            false,
            true,
            false,
            false,
            false,
            keyboardState);

    private static bool HasEditableAutomationHint(string controlType, string automationClassName, string focusClassName) =>
        controlType == ControlType.Document.ProgrammaticName ||
        controlType == ControlType.Pane.ProgrammaticName ||
        controlType == ControlType.Custom.ProgrammaticName ||
        automationClassName.Contains("Edit", StringComparison.OrdinalIgnoreCase) ||
        focusClassName.Contains("Chrome", StringComparison.OrdinalIgnoreCase) ||
        focusClassName.Contains("WebView", StringComparison.OrdinalIgnoreCase) ||
        focusClassName.Contains("Internet Explorer_Server", StringComparison.OrdinalIgnoreCase);

    private static string GetAutomationControlType(AutomationElement element)
    {
        try
        {
            return element.Current.ControlType?.ProgrammaticName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static int GetAutomationProcessId(AutomationElement element)
    {
        try
        {
            return element.Current.ProcessId;
        }
        catch
        {
            return 0;
        }
    }

    private static string GetAutomationStringProperty(AutomationElement element, AutomationProperty property)
    {
        try
        {
            var value = element.GetCurrentPropertyValue(property, ignoreDefaultValue: true);
            return value as string ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool GetAutomationBoolProperty(AutomationElement element, AutomationProperty property, bool defaultValue = false)
    {
        try
        {
            var value = element.GetCurrentPropertyValue(property, ignoreDefaultValue: true);
            return value is bool boolean ? boolean : defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    private static bool TryGetPattern(AutomationElement element, AutomationPattern pattern)
    {
        try
        {
            return element.TryGetCurrentPattern(pattern, out _);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetValuePattern(AutomationElement element, out ValuePattern? valuePattern)
    {
        valuePattern = null;
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && pattern is ValuePattern typedPattern)
            {
                valuePattern = typedPattern;
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

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

    private static bool WaitForBlockingModifiersToClear()
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(300);
        do
        {
            if (!HasBlockingModifierKeys(GetAsyncKeyState))
            {
                return true;
            }

            Thread.Sleep(20);
        }
        while (DateTime.UtcNow < deadline);

        return false;
    }

    internal static bool HasBlockingModifierKeys(Func<int, short> keyStateProvider) =>
        BlockingPasteModifiers.Any(key => IsKeyDown(keyStateProvider(key)));

    internal static bool IsKeyDown(short keyState) => (keyState & unchecked((short)0x8000)) != 0;

    private static string DescribeBlockingModifierState()
    {
        var down = BlockingPasteModifiers
            .Where(key => IsKeyDown(GetAsyncKeyState(key)))
            .Select(DescribeVirtualKey)
            .ToArray();
        return down.Length == 0 ? "Released" : string.Join("+", down);
    }

    private static string DescribeVirtualKey(int virtualKey) =>
        virtualKey switch
        {
            VK_MENU => "Alt",
            VK_RMENU => "RightAlt",
            VK_LCONTROL => "LeftCtrl",
            VK_RCONTROL => "RightCtrl",
            VK_LWIN => "LeftWin",
            VK_RWIN => "RightWin",
            _ => $"VK{virtualKey:X2}"
        };

    private static string? TryGetFocusedAutomationText(int processId)
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is null || GetAutomationProcessId(focused) != processId)
            {
                return null;
            }

            if (focused.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternObject) &&
                valuePatternObject is ValuePattern valuePattern &&
                !valuePattern.Current.IsReadOnly)
            {
                return valuePattern.Current.Value;
            }

            if (focused.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternObject) &&
                textPatternObject is TextPattern textPattern)
            {
                return textPattern.DocumentRange.GetText(32768);
            }
        }
        catch
        {
        }

        return null;
    }

    private static KeyboardPasteAttemptResult SendCtrlV()
    {
        var inputs = new[]
        {
            KeyboardInput(VK_CONTROL, 0),
            KeyboardInput(VK_V, 0),
            KeyboardInput(VK_V, KEYEVENTF_KEYUP),
            KeyboardInput(VK_CONTROL, KEYEVENTF_KEYUP)
        };

        var sentEvents = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        return new KeyboardPasteAttemptResult(sentEvents, Marshal.GetLastWin32Error(), true);
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

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

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

    internal static int NativeInputStructureSize => Marshal.SizeOf<INPUT>();

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;

        [FieldOffset(0)]
        public KEYBDINPUT ki;

        [FieldOffset(0)]
        public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
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
