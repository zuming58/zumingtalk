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
        Assert.Equal(55, stats.AverageCharactersPerMinute);
    }

    [Fact]
    public async Task SaveBailianCredentialsAsync_DoesNotStoreApiKeyInPlainText()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        var store = new SqliteStore(paths);
        var credentials = new BailianCredentialSettings("sk-super-secret");

        await store.SaveBailianCredentialsAsync(credentials, CancellationToken.None);

        var loaded = await store.GetBailianCredentialsAsync(CancellationToken.None);
        await using var connection = new SqliteConnection($"Data Source={paths.DatabasePath};Mode=ReadOnly");
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = 'bailian_api_key'";
        var storedSecret = (string?)await command.ExecuteScalarAsync(CancellationToken.None);

        Assert.Equal(credentials, loaded);
        Assert.NotEqual("sk-super-secret", storedSecret);
        Assert.False(string.IsNullOrWhiteSpace(storedSecret));
    }

    [Fact]
    public async Task CloudCredentialsAndDeviceFingerprint_AreProtectedAndStable()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        var store = new SqliteStore(paths);
        var credentials = new BailianCredentialSettings("sk-existing-user-key");

        await store.SaveBailianCredentialsAsync(credentials, CancellationToken.None);
        await store.SaveZumingtalkCloudCredentialsAsync(new ZumingtalkCloudCredentialSettings("https://service.example.test", "device-token-secret"), CancellationToken.None);
        var firstFingerprint = await store.GetOrCreateAsync(CancellationToken.None);
        var secondFingerprint = await store.GetOrCreateAsync(CancellationToken.None);
        var settings = await ((Domain.Services.ISettingsRepository)store).GetAsync(CancellationToken.None);

        await using var connection = new SqliteConnection($"Data Source={paths.DatabasePath};Mode=ReadOnly");
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = 'zumingtalk_cloud_device_token'";
        var storedToken = (string?)await command.ExecuteScalarAsync(CancellationToken.None);

        Assert.Equal(firstFingerprint, secondFingerprint);
        Assert.NotEqual("device-token-secret", storedToken);
        Assert.Equal("自有百炼 Key", settings.Recognition.Provider);
    }

    [Fact]
    public async Task BringYourOwnKey_RequiresAnActiveProEntitlement()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        var store = new SqliteStore(paths);
        var trialCloud = new FakeCloudAccountClient("Trial");
        var viewModel = new Application.Shell.ShellViewModel(store, store, store, null, paths, null, null, null, null, null, trialCloud)
        {
            RecognitionProvider = "自有百炼 Key"
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.EnsureBringYourOwnKeyAllowedAsync(CancellationToken.None));

        var proCloud = new FakeCloudAccountClient("Pro");
        var proViewModel = new Application.Shell.ShellViewModel(store, store, store, null, paths, null, null, null, null, null, proCloud)
        {
            RecognitionProvider = "自有百炼 Key"
        };
        await proViewModel.EnsureBringYourOwnKeyAllowedAsync(CancellationToken.None);

        Assert.True(proViewModel.HasActiveProEntitlement);
        Assert.True(proViewModel.CanEditBringYourOwnKey);
    }

    [Fact]
    public void FeedbackMailto_ContainsOnlyVersionAndOptionalDiagnostics()
    {
        var mailto = Application.Shell.ShellViewModel.BuildFeedbackMailto("support@example.test", "FG=Chrome Focus=Edit");
        var decoded = Uri.UnescapeDataString(mailto);

        Assert.Contains("support@example.test", decoded);
        Assert.Contains("版本：", decoded);
        Assert.Contains("FG=Chrome", decoded);
        Assert.DoesNotContain("final transcription", decoded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\recordings", decoded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk-super-secret", decoded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("device-token-secret", decoded, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ShellViewModel_SaveSettings_KeepsExistingApiKey_WhenPasswordFieldIsBlank()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        var store = new SqliteStore(paths);
        await store.SaveBailianCredentialsAsync(new BailianCredentialSettings("sk-old-secret"), CancellationToken.None);
        var viewModel = new Application.Shell.ShellViewModel(store, store, store, null, paths, null, new FakeAsrProviderFactory());

        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.BailianApiKey = string.Empty;

        await viewModel.SaveSettingsAsync();

        var loaded = await store.GetBailianCredentialsAsync(CancellationToken.None);
        Assert.Equal("sk-old-secret", loaded.ApiKey);
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
        viewModel.BailianApiKey = "sk-new-secret";

        await viewModel.TestConnectionAsync();

        var loaded = await store.GetBailianCredentialsAsync(CancellationToken.None);
        Assert.Equal(new BailianCredentialSettings("sk-new-secret"), loaded);
        Assert.True(factory.Provider.TestConnectionWasCalled);
    }

    [Fact]
    public async Task SaveSettingsAsync_PersistsSelectedMicrophone()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        var store = new SqliteStore(paths);
        var viewModel = new Application.Shell.ShellViewModel(
            store,
            store,
            store,
            null,
            paths,
            null,
            new FakeAsrProviderFactory(),
            new FakeMicrophoneDeviceService(),
            new FakeMicrophoneTestService());

        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.SelectedMicrophone = viewModel.Microphones.Single(device => device.DeviceNumber == 2);

        await viewModel.SaveSettingsAsync();

        var settings = await ((Domain.Services.ISettingsRepository)store).GetAsync(CancellationToken.None);
        Assert.Equal(2, settings.Recognition.MicrophoneDeviceNumber);
        Assert.Equal("USB Mic", settings.Recognition.MicrophoneName);
    }

    [Fact]
    public async Task SaveSettingsAsync_PersistsCompatibilityAndHotkeySettings()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        var store = new SqliteStore(paths);
        var viewModel = new Application.Shell.ShellViewModel(store, store, store, null, paths);

        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.SemanticPunctuationEnabled = false;
        viewModel.FallbackHotkeyEnabled = false;
        viewModel.PreferredInsertionMode = TextInsertionMethod.CopyOnly;

        await viewModel.SaveSettingsAsync();

        var settings = await ((Domain.Services.ISettingsRepository)store).GetAsync(CancellationToken.None);
        Assert.False(settings.Recognition.SemanticPunctuationEnabled);
        Assert.False(settings.Hotkeys.FallbackHotkeyEnabled);
        Assert.Equal(TextInsertionMethod.CopyOnly, settings.Compatibility.PreferredMode);
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
        await store.AddCompletedAsync(record.Duration, record.CharacterCount, CancellationToken.None);
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

        var stats = await ((Domain.Services.IStatisticsRepository)store).GetAsync(CancellationToken.None);
        Assert.Equal(8, stats.TotalCharacters);
    }

    [Fact]
    public async Task ShellViewModel_Retranscribe_RecalculatesStatisticsForPreviouslyFailedRecord()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        var store = new SqliteStore(paths);
        var audioPath = Path.Combine(paths.RecordingsDirectory, "failed.wav");
        await File.WriteAllTextAsync(audioPath, "wav", CancellationToken.None);
        var record = new TranscriptionRecord(
            Guid.NewGuid(),
            TranscriptionStatus.Failed,
            DateTimeOffset.Now,
            TimeSpan.FromSeconds(5),
            string.Empty,
            audioPath,
            "Aliyun",
            "task",
            0,
            0);
        await store.UpsertAsync(record, CancellationToken.None);
        await store.AddFailedDurationAsync(record.Duration, CancellationToken.None);
        var factory = new FakeAsrProviderFactory { RetranscribeText = "fresh text" };
        var viewModel = new Application.Shell.ShellViewModel(store, store, store, null, paths, null, factory);

        await viewModel.InitializeAsync(CancellationToken.None);
        await viewModel.RetranscribeRecordAsync(viewModel.Records[0], CancellationToken.None);

        var stats = await ((Domain.Services.IStatisticsRepository)store).GetAsync(CancellationToken.None);
        Assert.Equal("fresh text", (await store.GetAsync(record.Id, CancellationToken.None))!.FinalText);
        Assert.Equal("fresh text".Length, stats.TotalCharacters);
        Assert.Equal(TimeSpan.FromSeconds(5), stats.TotalDuration);
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

        public Domain.Services.IAsrProvider Create(BailianCredentialSettings credentials, bool semanticPunctuationEnabled)
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

    private sealed class FakeMicrophoneDeviceService : Domain.Services.IMicrophoneDeviceService
    {
        public IReadOnlyList<Domain.Services.MicrophoneDevice> ListDevices() =>
        [
            new Domain.Services.MicrophoneDevice(0, "Default"),
            new Domain.Services.MicrophoneDevice(2, "USB Mic")
        ];
    }

    private sealed class FakeMicrophoneTestService : Domain.Services.IMicrophoneTestService
    {
        public Task TestAsync(int deviceNumber, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeCloudAccountClient(string plan) : Domain.Services.ICloudAccountClient
    {
        public Task ActivateAsync(string serviceBaseUrl, string inviteCode, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<Domain.Services.CloudEntitlementSnapshot> GetEntitlementAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new Domain.Services.CloudEntitlementSnapshot(plan, DateTimeOffset.UtcNow, []));
    }
}
