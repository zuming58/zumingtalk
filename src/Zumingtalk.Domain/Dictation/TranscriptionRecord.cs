namespace Zumingtalk.Domain.Dictation;

public sealed record TranscriptionRecord(
    Guid Id,
    TranscriptionStatus Status,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    string FinalText,
    string? AudioPath,
    string Provider,
    string? ProviderTaskId,
    int RetryCount,
    int CharacterCount,
    TextInsertionMethod InsertionMethod = TextInsertionMethod.Auto,
    string Source = "直接说");
