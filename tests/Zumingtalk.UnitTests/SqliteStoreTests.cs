using Microsoft.Data.Sqlite;
using Zumingtalk.Application.History;
using Zumingtalk.Domain.Dictation;
using Zumingtalk.Domain.Settings;
using Zumingtalk.Infrastructure.Storage;

namespace Zumingtalk.UnitTests;

public sealed class SqliteStoreTests
{
    [Fact]
    public async Task UpsertAndListRecentAsync_PersistsRecordsAndStatistics()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        var store = new SqliteStore(paths);
        var record = new TranscriptionRecord(
            Guid.NewGuid(),
            TranscriptionStatus.Completed,
            DateTimeOffset.Now,
            TimeSpan.FromSeconds(12),
            "hello world",
            Path.Combine(paths.RecordingsDirectory, "one.wav"),
            "Aliyun",
            "task-1",
            1,
            11,
            TextInsertionMethod.NativeReplaceSelection);

        await store.InitializeAsync(CancellationToken.None);
        await store.UpsertAsync(record, CancellationToken.None);
        await store.AddCompletedAsync(record.Duration, record.CharacterCount, CancellationToken.None);

        var records = await store.ListRecentAsync(3, CancellationToken.None);
        var stats = await ((Domain.Services.IStatisticsRepository)store).GetAsync(CancellationToken.None);

        Assert.Single(records);
        Assert.Equal(record.Id, records[0].Id);
        Assert.Equal(11, stats.TotalCharacters);
        Assert.Equal(TimeSpan.FromSeconds(12), stats.TotalDuration);
    }

    [Fact]
    public async Task SaveAliyunCredentialsAsync_DoesNotStoreSecretInPlainText()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        var store = new SqliteStore(paths);
        var credentials = new AliyunCredentialSettings("app-key", "access-id", "super-secret");

        await store.SaveAliyunCredentialsAsync(credentials, CancellationToken.None);

        var loaded = await store.GetAliyunCredentialsAsync(CancellationToken.None);
        await using var connection = new SqliteConnection($"Data Source={paths.DatabasePath};Mode=ReadOnly");
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = 'aliyun_access_key_secret'";
        var storedSecret = (string?)await command.ExecuteScalarAsync(CancellationToken.None);

        Assert.Equal(credentials, loaded);
        Assert.NotEqual("super-secret", storedSecret);
        Assert.False(string.IsNullOrWhiteSpace(storedSecret));
    }

    [Fact]
    public async Task RetentionService_RemovesOnlyExpiredUnreferencedWavFiles()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        var store = new SqliteStore(paths);
        var keepPath = Path.Combine(paths.RecordingsDirectory, "keep.wav");
        var removePath = Path.Combine(paths.RecordingsDirectory, "remove.wav");
        await File.WriteAllTextAsync(keepPath, "keep", CancellationToken.None);
        await File.WriteAllTextAsync(removePath, "remove", CancellationToken.None);
        File.SetCreationTime(removePath, DateTime.Now.AddDays(-5));

        await store.UpsertAsync(new TranscriptionRecord(
            Guid.NewGuid(),
            TranscriptionStatus.Completed,
            DateTimeOffset.Now,
            TimeSpan.FromSeconds(1),
            "kept",
            keepPath,
            "Aliyun",
            null,
            0,
            4), CancellationToken.None);

        await new RetentionService(store, paths).CleanupAsync(3, CancellationToken.None);

        Assert.True(File.Exists(keepPath));
        Assert.False(File.Exists(removePath));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zumingtalk-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            // Intentionally left on disk: project instructions prohibit batch directory deletion.
        }
    }
}
