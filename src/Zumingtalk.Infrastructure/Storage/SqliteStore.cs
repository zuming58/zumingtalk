using System.Globalization;
using Microsoft.Data.Sqlite;
using Zumingtalk.Domain.Dictation;
using Zumingtalk.Domain.Services;
using Zumingtalk.Domain.Settings;

namespace Zumingtalk.Infrastructure.Storage;

public sealed class SqliteStore : IHistoryRepository, IStatisticsRepository, ISettingsRepository, IDeviceFingerprintProvider
{
    private readonly IAppPaths appPaths;

    public SqliteStore(IAppPaths appPaths)
    {
        this.appPaths = appPaths;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var commands = new[]
        {
            """
            CREATE TABLE IF NOT EXISTS records (
                id TEXT PRIMARY KEY,
                status TEXT NOT NULL,
                started_at TEXT NOT NULL,
                duration_ms INTEGER NOT NULL,
                final_text TEXT NOT NULL,
                audio_path TEXT,
                provider TEXT NOT NULL,
                provider_task_id TEXT,
                retry_count INTEGER NOT NULL,
                character_count INTEGER NOT NULL,
                insertion_method TEXT NOT NULL,
                source TEXT NOT NULL,
                error_message TEXT
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS statistics (
                key TEXT PRIMARY KEY,
                value INTEGER NOT NULL
            );
            """,
            "INSERT OR IGNORE INTO statistics(key, value) VALUES ('total_duration_ms', 0), ('total_characters', 0), ('successful_duration_ms', 0);"
        };

        foreach (var sql in commands)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<TranscriptionRecord>> ListRecentAsync(int days, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, status, started_at, duration_ms, final_text, audio_path, provider, provider_task_id,
                   retry_count, character_count, insertion_method, source, error_message
            FROM records
            WHERE datetime(started_at) >= datetime($cutoff)
            ORDER BY datetime(started_at) DESC
            """;
        command.Parameters.AddWithValue("$cutoff", DateTimeOffset.Now.Date.AddDays(-(days - 1)).ToString("O", CultureInfo.InvariantCulture));

        var records = new List<TranscriptionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(ReadRecord(reader));
        }

        return records;
    }

    public async Task<TranscriptionRecord?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, status, started_at, duration_ms, final_text, audio_path, provider, provider_task_id,
                   retry_count, character_count, insertion_method, source, error_message
            FROM records
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRecord(reader) : null;
    }

    public async Task UpsertAsync(TranscriptionRecord record, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO records (
                id, status, started_at, duration_ms, final_text, audio_path, provider, provider_task_id,
                retry_count, character_count, insertion_method, source, error_message
            ) VALUES (
                $id, $status, $started_at, $duration_ms, $final_text, $audio_path, $provider, $provider_task_id,
                $retry_count, $character_count, $insertion_method, $source, $error_message
            )
            ON CONFLICT(id) DO UPDATE SET
                status = excluded.status,
                duration_ms = excluded.duration_ms,
                final_text = excluded.final_text,
                audio_path = excluded.audio_path,
                provider = excluded.provider,
                provider_task_id = excluded.provider_task_id,
                retry_count = excluded.retry_count,
                character_count = excluded.character_count,
                insertion_method = excluded.insertion_method,
                source = excluded.source,
                error_message = excluded.error_message
            """;
        command.Parameters.AddWithValue("$id", record.Id.ToString());
        command.Parameters.AddWithValue("$status", record.Status.ToString());
        command.Parameters.AddWithValue("$started_at", record.StartedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$duration_ms", (long)record.Duration.TotalMilliseconds);
        command.Parameters.AddWithValue("$final_text", record.FinalText);
        command.Parameters.AddWithValue("$audio_path", (object?)record.AudioPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$provider", record.Provider);
        command.Parameters.AddWithValue("$provider_task_id", (object?)record.ProviderTaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("$retry_count", record.RetryCount);
        command.Parameters.AddWithValue("$character_count", record.CharacterCount);
        command.Parameters.AddWithValue("$insertion_method", record.InsertionMethod.ToString());
        command.Parameters.AddWithValue("$source", record.Source);
        command.Parameters.AddWithValue("$error_message", (object?)record.ErrorMessage ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM records WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM records WHERE datetime(started_at) < datetime($cutoff)";
        command.Parameters.AddWithValue("$cutoff", cutoff.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    async Task<DictationStatistics> IStatisticsRepository.GetAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        var totalDuration = await GetStatAsync("total_duration_ms", cancellationToken);
        var totalCharacters = await GetStatAsync("total_characters", cancellationToken);
        var successfulDuration = await GetStatAsync("successful_duration_ms", cancellationToken);
        var minutes = TimeSpan.FromMilliseconds(successfulDuration).TotalMinutes;
        var averageSpeed = minutes <= 0 ? 0 : (int)Math.Round(totalCharacters / minutes);
        return new DictationStatistics(TimeSpan.FromMilliseconds(totalDuration), (int)totalCharacters, averageSpeed);
    }

    public async Task AddCompletedAsync(TimeSpan duration, int characterCount, CancellationToken cancellationToken)
    {
        await AddStatAsync("total_duration_ms", (long)duration.TotalMilliseconds, cancellationToken);
        await AddStatAsync("successful_duration_ms", (long)duration.TotalMilliseconds, cancellationToken);
        await AddStatAsync("total_characters", characterCount, cancellationToken);
    }

    public Task AddFailedDurationAsync(TimeSpan duration, CancellationToken cancellationToken) =>
        AddStatAsync("total_duration_ms", (long)duration.TotalMilliseconds, cancellationToken);

    public async Task AdjustCompletedAsync(TimeSpan totalDurationDelta, TimeSpan successfulDurationDelta, int characterCountDelta, CancellationToken cancellationToken)
    {
        await AddStatAsync("total_duration_ms", (long)totalDurationDelta.TotalMilliseconds, cancellationToken);
        await AddStatAsync("successful_duration_ms", (long)successfulDurationDelta.TotalMilliseconds, cancellationToken);
        await AddStatAsync("total_characters", characterCountDelta, cancellationToken);
    }

    Task<AppSettings> ISettingsRepository.GetAsync(CancellationToken cancellationToken)
    {
        var credentials = GetBailianCredentialsAsync(cancellationToken);
        return BuildSettingsAsync(credentials, cancellationToken);
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await SetSettingAsync("semantic_punctuation", settings.Recognition.SemanticPunctuationEnabled ? "true" : "false", cancellationToken);
        await SetSettingAsync("microphone_device_number", settings.Recognition.MicrophoneDeviceNumber.ToString(CultureInfo.InvariantCulture), cancellationToken);
        await SetSettingAsync("microphone_name", settings.Recognition.MicrophoneName, cancellationToken);
        await SetSettingAsync("fallback_hotkey_enabled", settings.Hotkeys.FallbackHotkeyEnabled ? "true" : "false", cancellationToken);
        await SetSettingAsync("preferred_insertion_mode", settings.Compatibility.PreferredMode.ToString(), cancellationToken);
        await SetSettingAsync("recognition_provider", settings.Recognition.Provider, cancellationToken);
        await SetSettingAsync("support_email", settings.SupportEmail, cancellationToken);
    }

    public async Task<AliyunCredentialSettings> GetAliyunCredentialsAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        var appKey = await GetSettingAsync("aliyun_appkey", cancellationToken) ?? string.Empty;
        var accessKeyId = await GetSettingAsync("aliyun_access_key_id", cancellationToken) ?? string.Empty;
        var encryptedSecret = await GetSettingAsync("aliyun_access_key_secret", cancellationToken);
        var secret = string.IsNullOrWhiteSpace(encryptedSecret) ? string.Empty : ProtectedSecret.Unprotect(encryptedSecret);
        return new AliyunCredentialSettings(appKey, accessKeyId, secret);
    }

    public async Task SaveAliyunCredentialsAsync(AliyunCredentialSettings credentials, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await SetSettingAsync("aliyun_appkey", credentials.AppKey, cancellationToken);
        await SetSettingAsync("aliyun_access_key_id", credentials.AccessKeyId, cancellationToken);
        await SetSettingAsync("aliyun_access_key_secret", ProtectedSecret.Protect(credentials.AccessKeySecret), cancellationToken);
    }

    public async Task<BailianCredentialSettings> GetBailianCredentialsAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        var encryptedApiKey = await GetSettingAsync("bailian_api_key", cancellationToken);
        var apiKey = string.IsNullOrWhiteSpace(encryptedApiKey) ? string.Empty : ProtectedSecret.Unprotect(encryptedApiKey);
        var model = await GetSettingAsync("bailian_model", cancellationToken) ?? "fun-asr-realtime";
        var endpoint = await GetSettingAsync("bailian_endpoint", cancellationToken) ?? "wss://dashscope.aliyuncs.com/api-ws/v1/inference";
        return new BailianCredentialSettings(apiKey, model, endpoint);
    }

    public async Task SaveBailianCredentialsAsync(BailianCredentialSettings credentials, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await SetSettingAsync("bailian_api_key", ProtectedSecret.Protect(credentials.ApiKey), cancellationToken);
        await SetSettingAsync("bailian_model", credentials.Model, cancellationToken);
        await SetSettingAsync("bailian_endpoint", credentials.Endpoint, cancellationToken);
    }

    public async Task<ZumingtalkCloudCredentialSettings> GetZumingtalkCloudCredentialsAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        var serviceBaseUrl = await GetSettingAsync("zumingtalk_cloud_service_url", cancellationToken) ?? string.Empty;
        var encryptedToken = await GetSettingAsync("zumingtalk_cloud_device_token", cancellationToken);
        var token = string.IsNullOrWhiteSpace(encryptedToken) ? string.Empty : ProtectedSecret.Unprotect(encryptedToken);
        return new ZumingtalkCloudCredentialSettings(serviceBaseUrl, token);
    }

    public async Task SaveZumingtalkCloudCredentialsAsync(ZumingtalkCloudCredentialSettings credentials, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await SetSettingAsync("zumingtalk_cloud_service_url", credentials.ServiceBaseUrl.TrimEnd('/'), cancellationToken);
        await SetSettingAsync("zumingtalk_cloud_device_token", ProtectedSecret.Protect(credentials.DeviceToken), cancellationToken);
    }

    public async Task<string> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        var encryptedFingerprint = await GetSettingAsync("zumingtalk_device_fingerprint", cancellationToken);
        if (!string.IsNullOrWhiteSpace(encryptedFingerprint))
        {
            return ProtectedSecret.Unprotect(encryptedFingerprint);
        }

        var fingerprint = Guid.NewGuid().ToString("N");
        await SetSettingAsync("zumingtalk_device_fingerprint", ProtectedSecret.Protect(fingerprint), cancellationToken);
        return fingerprint;
    }

    private async Task<AppSettings> BuildSettingsAsync(Task<BailianCredentialSettings> credentialsTask, CancellationToken cancellationToken)
    {
        var credentials = await credentialsTask;
        var microphoneName = await GetSettingAsync("microphone_name", cancellationToken) ?? "系统默认麦克风";
        var microphoneDeviceNumberText = await GetSettingAsync("microphone_device_number", cancellationToken) ?? "0";
        var semanticPunctuationText = await GetSettingAsync("semantic_punctuation", cancellationToken)
            ?? await GetSettingAsync("oral_smoothing", cancellationToken)
            ?? "true";
        var insertionModeText = await GetSettingAsync("preferred_insertion_mode", cancellationToken) ?? TextInsertionMethod.Auto.ToString();
        var provider = await GetSettingAsync("recognition_provider", cancellationToken)
            ?? (string.IsNullOrWhiteSpace(credentials.ApiKey) ? "祖名云端识别" : "自有百炼 Key");
        var supportEmail = await GetSettingAsync("support_email", cancellationToken) ?? string.Empty;
        _ = int.TryParse(microphoneDeviceNumberText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var microphoneDeviceNumber);
        _ = bool.TryParse(semanticPunctuationText, out var semanticPunctuationEnabled);
        if (!Enum.TryParse<TextInsertionMethod>(insertionModeText, out var preferredMode))
        {
            preferredMode = TextInsertionMethod.Auto;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new AppSettings(
            new RecognitionSettings(
                provider,
                Mask(credentials.ApiKey),
                SemanticPunctuationEnabled: semanticPunctuationEnabled,
                MicrophoneName: microphoneName,
                MicrophoneDeviceNumber: microphoneDeviceNumber),
            new HotkeySettings("右 Alt", false, string.Empty),
            new CompatibilitySettings("尚未捕获", TextInsertionMethod.Auto, false, preferredMode),
            new LocalDataSettings(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zumingtalk", "recordings"), 3),
            supportEmail);
    }

    private static string Mask(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "未配置";
        }

        return value.Length <= 6 ? "••••••" : $"{value[..Math.Min(4, value.Length)]}••••{value[^Math.Min(4, value.Length)..]}";
    }

    private async Task<long> GetStatAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM statistics WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private async Task AddStatAsync(string key, long value, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO statistics(key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = value + excluded.value";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value as string;
    }

    private async Task SetSettingAsync(string key, string value, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO settings(key, value) VALUES($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(appPaths.DatabasePath)!);
        var connection = new SqliteConnection($"Data Source={appPaths.DatabasePath}");
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static TranscriptionRecord ReadRecord(SqliteDataReader reader)
    {
        return new TranscriptionRecord(
            Guid.Parse(reader.GetString(0)),
            Enum.Parse<TranscriptionStatus>(reader.GetString(1)),
            DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            TimeSpan.FromMilliseconds(reader.GetInt64(3)),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            Enum.Parse<TextInsertionMethod>(reader.GetString(10)),
            reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12));
    }
}
