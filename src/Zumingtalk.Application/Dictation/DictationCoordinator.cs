using Zumingtalk.Domain.Dictation;
using Zumingtalk.Domain.Services;

namespace Zumingtalk.Application.Dictation;

public sealed class DictationCoordinator
{
    private readonly IAudioRecorder audioRecorder;
    private readonly IAsrProvider asrProvider;
    private readonly ITextInsertionService textInsertionService;
    private CapturedInputTarget? capturedTarget;

    public DictationCoordinator(
        IAudioRecorder audioRecorder,
        IAsrProvider asrProvider,
        ITextInsertionService textInsertionService)
    {
        this.audioRecorder = audioRecorder;
        this.asrProvider = asrProvider;
        this.textInsertionService = textInsertionService;
    }

    public DictationState State { get; private set; } = DictationState.Idle;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        capturedTarget = textInsertionService.CaptureCurrentTarget();
        await audioRecorder.StartAsync(cancellationToken);
        State = DictationState.Recording;
    }

    public async Task CancelAsync(CancellationToken cancellationToken)
    {
        await audioRecorder.CancelAsync(cancellationToken);
        capturedTarget = null;
        State = DictationState.Cancelled;
    }

    public async Task<DictationCompletionResult> FinishAsync(CancellationToken cancellationToken)
    {
        if (capturedTarget is null)
        {
            throw new InvalidOperationException("Dictation has not captured a target.");
        }

        State = DictationState.Recognizing;
        var recording = await audioRecorder.StopAsync(cancellationToken);
        var text = await asrProvider.RetranscribeAsync(recording.AudioPath, cancellationToken);

        if (capturedTarget.Kind != InputTargetKind.Editable)
        {
            State = DictationState.Saved;
            return new DictationCompletionResult(text, TextInsertionMethod.CopyOnly, CapturedKind: capturedTarget.Kind, Inserted: false);
        }

        var insertion = await textInsertionService.InsertAsync(capturedTarget, text, cancellationToken);
        State = insertion.Succeeded ? DictationState.Completed : DictationState.InsertionBlocked;

        return new DictationCompletionResult(text, insertion.Method, capturedTarget.Kind, insertion.Succeeded);
    }
}

public sealed record DictationCompletionResult(
    string FinalText,
    TextInsertionMethod InsertionMethod,
    InputTargetKind CapturedKind,
    bool Inserted);
