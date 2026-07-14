using Zumingtalk.Domain.Dictation;

namespace Zumingtalk.Domain.Services;

public interface ITextInsertionService
{
    CapturedInputTarget CaptureCurrentTarget();

    CapturedInputTarget ValidateCapturedTarget(CapturedInputTarget capturedTarget);

    Task<TextInsertionResult> InsertAsync(CapturedInputTarget target, string text, CancellationToken cancellationToken);

    Task<TextInsertionResult> CopyOnlyAsync(string text, CancellationToken cancellationToken);
}

public sealed record CapturedInputTarget(
    InputTargetKind Kind,
    IntPtr WindowHandle,
    IntPtr FocusHandle,
    int ProcessId,
    string ProcessName,
    string IntegrityLevel,
    string ClassName = "",
    bool IsElevated = false,
    InputTargetDiagnostics? Diagnostics = null);

public sealed record InputTargetDiagnostics(
    string ForegroundClassName,
    string FocusClassName,
    IntPtr CaretHandle,
    string AutomationControlType,
    string AutomationClassName,
    string AutomationId,
    bool AutomationIsKeyboardFocusable,
    bool AutomationIsEnabled,
    bool SupportsValuePattern,
    bool SupportsTextPattern,
    bool IsEditableCandidate,
    string KeyboardState,
    string Strategy = "",
    uint SendInputEvents = 0,
    int LastWin32Error = 0,
    bool KeepsClipboardFallback = false);

public sealed record TextInsertionResult(
    bool Succeeded,
    TextInsertionMethod Method,
    string Message,
    InputTargetDiagnostics? Diagnostics = null);
