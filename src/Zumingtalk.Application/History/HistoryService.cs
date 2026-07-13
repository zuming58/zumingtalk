using Zumingtalk.Domain.Dictation;
using Zumingtalk.Domain.Services;

namespace Zumingtalk.Application.History;

public sealed class HistoryService
{
    private readonly IHistoryRepository historyRepository;
    private readonly IStatisticsRepository statisticsRepository;

    public HistoryService(IHistoryRepository historyRepository, IStatisticsRepository statisticsRepository)
    {
        this.historyRepository = historyRepository;
        this.statisticsRepository = statisticsRepository;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await historyRepository.InitializeAsync(cancellationToken);
    }

    public Task<IReadOnlyList<TranscriptionRecord>> ListRecentAsync(int retentionDays, CancellationToken cancellationToken) =>
        historyRepository.ListRecentAsync(retentionDays, cancellationToken);

    public async Task SaveCompletedAsync(TranscriptionRecord record, CancellationToken cancellationToken)
    {
        await historyRepository.UpsertAsync(record, cancellationToken);

        if (record.Status == TranscriptionStatus.Completed)
        {
            await statisticsRepository.AddCompletedAsync(record.Duration, record.CharacterCount, cancellationToken);
        }
        else if (record.Status == TranscriptionStatus.Failed)
        {
            await statisticsRepository.AddFailedDurationAsync(record.Duration, cancellationToken);
        }
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        historyRepository.DeleteAsync(id, cancellationToken);
}
