using Zumingtalk.Domain.Dictation;

namespace Zumingtalk.Domain.Services;

public interface IHistoryRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<TranscriptionRecord>> ListRecentAsync(int days, CancellationToken cancellationToken);

    Task<TranscriptionRecord?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task UpsertAsync(TranscriptionRecord record, CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);

    Task DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}
