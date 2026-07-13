using Zumingtalk.Domain.Dictation;
using Zumingtalk.Domain.Services;

namespace Zumingtalk.Infrastructure.Windows;

public sealed class WindowsTextInsertionServiceStub : ITextInsertionService
{
    public CapturedInputTarget CaptureCurrentTarget() =>
        new(InputTargetKind.None, IntPtr.Zero, IntPtr.Zero, 0, "NoTarget", "Medium");

    public Task<TextInsertionResult> InsertAsync(CapturedInputTarget target, string text, CancellationToken cancellationToken) =>
        Task.FromResult(new TextInsertionResult(false, TextInsertionMethod.CopyFallback, "M1 stub keeps text in history."));
}
