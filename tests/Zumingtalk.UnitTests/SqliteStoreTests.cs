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
    public async Task ShellViewModel_SaveSettings_KeepsExistingSecret_WhenSecretFieldIsBlank()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        var store = new SqliteStore(paths);
        await store.SaveAliyunCredentialsAsync(new AliyunCredentialSettings("old-app", "old-id", "old-secret"), CancellationToken.None);
        var viewModel = new Application.Shell.ShellViewModel(store, store, store, null, paths, null, new FakeAsrProviderFactory());

        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.AliyunAppKey = "new-app";
        viewModel.AliyunAccessKeyId = "new-id";
        viewModel.AliyunAccessKeySecret = string.Empty;

        await viewModel.SaveSettingsAsync();

        var loaded = await store.GetAliyunCredentialsAsync(CancellationToken.None);
        Assert.Equal("new-app", loaded.AppKey);
        Assert.Equal("new-id", loaded.AccessKeyId);
        Assert.Equal("old-secret", loaded.AccessKeySecret);
    }

    [Fact]
    public async Task ShellViewModel_TestConnection_SavesCredentialsAndCallsProvider()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        var store = new SqliteStore(paths);
        var factory = new FakeAsrProviderFactory();
        var viewModel = new Application.Shell.ShellViewModel(store, store, store, null, paths, null, factory);

        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.AliyunAppKey = "app";
        viewModel.AliyunAccessKeyId = "id";
        viewModel.AliyunAccessKeySecret = "secret";

        await viewModel.TestConnectionAsync();

        var loaded = await store.GetAliyunCredentialsAsync(CancellationToken.None);
        Assert.Equal(new AliyunCredentialSettings("app", "id", "secret"), loaded);
        Assert.True(factory.Provider.TestConnectionWasCalled);
    }

    [Fact]
    public async Task ShellViewModel_Retranscribe_UpdatesExistingRecordWithAliyunResult()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        var store = new SqliteStore(paths);
        var audioPath = Path.Combine(paths.RecordingsDirectory, "record.wav");
        await File.WriteAllTextAsync(audioPath, "wav", CancellationToken.None);
        var record = new TranscriptionRecord(
            Guid.NewGuid(),
            TranscriptionStatus.Completed,
            DateTimeOffset.Now,
            TimeSpan.FromSeconds(5),
            "old",
            audioPath,
            "Aliyun",
            "task",
            0,
            3);
        await store.UpsertAsync(record, CancellationToken.None);
        var factory = new FakeAsrProviderFactory { RetranscribeText = "new text" };
        var viewModel = new Application.Shell.ShellViewModel(store, store, store, null, paths, null, factory);

        await viewModel.InitializeAsync(CancellationToken.None);
        await viewModel.RetranscribeRecordAsync(viewModel.Records[0], CancellationToken.None);

        var updated = await store.GetAsync(record.Id, CancellationToken.None);
        Assert.NotNull(updated);
        Assert.Equal("new text", updated.FinalText);
        Assert.Equal(1, updated.RetryCount);
        Assert.Equal(TranscriptionStatus.Completed, updated.Status);
        Assert.True(factory.Provider.RetranscribeWasCalled);
    }

    [Fact]
    public async Task RetentionService_RemovesExpiredRecordsAndUnreferencedWavFiles()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        var store = new SqliteStore(paths);
        var keepPath = Path.Combine(paths.RecordingsDirectory, "keep.wav");
        var expiredPath = Path.Combine(paths.RecordingsDirectory, "expired.wav");
        var removePath = Path.Combine(paths.RecordingsDirectory, "remove.wav");
        await File.WriteAllTextAsync(keepPath, "keep", CancellationToken.None);
        await File.WriteAllTextAsync(expiredPath, "expired", CancellationToken.None);
        await File.WriteAllTextAsync(removePath, "remove", CancellationToken.None);
        File.SetCreationTime(expiredPath, DateTime.Now.AddDays(-5));
        File.SetCreationTime(removePath, DateTime.Now.AddDays(-5));
        var expiredId = Guid.NewGuid();

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
        await store.UpsertAsync(new TranscriptionRecord(
            expiredId,
            TranscriptionStatus.Completed,
            DateTimeOffset.Now.AddDays(-5),
            TimeSpan.FromSeconds(1),
            "expired",
            expiredPath,
            "Aliyun",
            null,
            0,
            7), CancellationToken.None);
        await store.AddCompletedAsync(TimeSpan.FromSeconds(1), 7, CancellationToken.None);

        await new RetentionService(store, paths).CleanupAsync(3, CancellationToken.None);

        Assert.True(File.Exists(keepPath));
        Assert.False(File.Exists(expiredPath));
        Assert.False(File.Exists(removePath));
        Assert.Null(await store.GetAsync(expiredId, CancellationToken.None));
        var stats = await ((Domain.Services.IStatisticsRepository)store).GetAsync(CancellationToken.None);
        Assert.Equal(7, stats.TotalCharacters);
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

    private sealed class FakeAsrProviderFactory : Domain.Services.IAsrProviderFactory
    {
        public string RetranscribeText { get; set; } = string.Empty;

        public FakeAsrProvider Provider { get; } = new();

        public Domain.Services.IAsrProvider Create(AliyunCredentialSettings credentials)
        {
            Provider.RetranscribeText = RetranscribeText;
            return Provider;
        }
    }

    private sealed class FakeAsrProvider : Domain.Services.IAsrProvider
    {
        public bool TestConnectionWasCalled { get; private set; }
        public bool RetranscribeWasCalled { get; private set; }
        public string RetranscribeText { get; set; } = string.Empty;

        public Task TestConnectionAsync(CancellationToken cancellationToken)
        {
            TestConnectionWasCalled = true;
            return Task.CompletedTask;
        }

        public Task<Domain.Services.IAsrSession> StartSessionAsync(CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<string> RetranscribeAsync(string audioPath, CancellationToken cancellationToken)
        {
            RetranscribeWasCalled = true;
            return Task.FromResult(RetranscribeText);
        }
    }
}
