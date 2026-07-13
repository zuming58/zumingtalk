using Zumingtalk.Domain.Services;

namespace Zumingtalk.Application.History;

public sealed class RetentionService
{
    private readonly IHistoryRepository historyRepository;
    private readonly IAppPaths appPaths;

    public RetentionService(IHistoryRepository historyRepository, IAppPaths appPaths)
    {
        this.historyRepository = historyRepository;
        this.appPaths = appPaths;
    }

    public async Task CleanupAsync(int retentionDays, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(appPaths.RecordingsDirectory);
        var keepRecords = await historyRepository.ListRecentAsync(retentionDays, cancellationToken);
        var keepAudioPaths = keepRecords
            .Select(record => record.AudioPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var cutoff = DateTimeOffset.Now.Date.AddDays(-(retentionDays - 1));
        await historyRepository.DeleteOlderThanAsync(cutoff, cancellationToken);

        foreach (var file in Directory.EnumerateFiles(appPaths.RecordingsDirectory, "*.wav", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fullPath = Path.GetFullPath(file);
            if (keepAudioPaths.Contains(fullPath))
            {
                continue;
            }

            var created = File.GetCreationTime(file);
            if (created < cutoff)
            {
                File.Delete(file);
            }
        }
    }
}
