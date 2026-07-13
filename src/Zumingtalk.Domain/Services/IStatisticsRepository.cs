using Zumingtalk.Domain.Dictation;

namespace Zumingtalk.Domain.Services;

public interface IStatisticsRepository
{
    Task<DictationStatistics> GetAsync(CancellationToken cancellationToken);

    Task AddCompletedAsync(TimeSpan duration, int characterCount, CancellationToken cancellationToken);

    Task AddFailedDurationAsync(TimeSpan duration, CancellationToken cancellationToken);

    Task AdjustCompletedAsync(TimeSpan totalDurationDelta, TimeSpan successfulDurationDelta, int characterCountDelta, CancellationToken cancellationToken);
}
