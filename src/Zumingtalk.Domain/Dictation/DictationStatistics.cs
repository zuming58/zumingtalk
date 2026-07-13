namespace Zumingtalk.Domain.Dictation;

public sealed record DictationStatistics(
    TimeSpan TotalDuration,
    int TotalCharacters,
    int AverageCharactersPerMinute);
