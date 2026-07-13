namespace Zumingtalk.Domain.Dictation;

public enum TranscriptionStatus
{
    Recording,
    Recognizing,
    Completed,
    Failed,
    Cancelled
}

public enum DictationState
{
    Idle,
    Recording,
    Recognizing,
    Completed,
    Saved,
    InsertionBlocked,
    Failed,
    Cancelled
}

public enum InputTargetKind
{
    Editable,
    None,
    Lost
}

public enum TextInsertionMethod
{
    Auto,
    NativeReplaceSelection,
    PasteMessage,
    SendInputPaste,
    CopyFallback,
    CopyOnly
}
