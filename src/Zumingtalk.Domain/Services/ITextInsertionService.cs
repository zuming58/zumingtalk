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
    bool IsElevated = false);

public sealed record TextInsertionResult(
    bool Succeeded,
    TextInsertionMethod Method,
    string Message);
