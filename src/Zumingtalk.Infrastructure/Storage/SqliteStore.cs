using System.Globalization;
using Microsoft.Data.Sqlite;
using Zumingtalk.Domain.Dictation;
using Zumingtalk.Domain.Services;
using Zumingtalk.Domain.Settings;

namespace Zumingtalk.Infrastructure.Storage;

public sealed class SqliteStore : IHistoryRepository, IStatisticsRepository, ISettingsRepository
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

    async Task<DictationStatistics> IStatisticsRepository.GetAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        var totalDuration = await GetStatAsync("total_duration_ms", cancellationToken);
        var totalCharacters = await GetStatAsync("total_characters", cancellationToken);
        var successfulDuration = await GetStatAsync("successful_duration_ms", cancellationToken);
        var minutes = Math.Max(1, TimeSpan.FromMilliseconds(successfulDuration).TotalMinutes);
        return new DictationStatistics(TimeSpan.FromMilliseconds(totalDuration), (int)totalCharacters, (int)Math.Round(totalCharacters / minutes));
    }

    public async Task AddCompletedAsync(TimeSpan duration, int characterCount, CancellationToken cancellationToken)
    {
        await AddStatAsync("total_duration_ms", (long)duration.TotalMilliseconds, cancellationToken);
        await AddStatAsync("successful_duration_ms", (long)duration.TotalMilliseconds, cancellationToken);
        await AddStatAsync("total_characters", characterCount, cancellationToken);
    }

    public Task AddFailedDurationAsync(TimeSpan duration, CancellationToken cancellationToken) =>
        AddStatAsync("total_duration_ms", (long)duration.TotalMilliseconds, cancellationToken);

    Task<AppSettings> ISettingsRepository.GetAsync(CancellationToken cancellationToken)
    {
        var credentials = GetAliyunCredentialsAsync(cancellationToken);
        return BuildSettingsAsync(credentials, cancellationToken);
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await SetSettingAsync("oral_smoothing", settings.Recognition.OralSmoothingEnabled ? "true" : "false", cancellationToken);
        await SetSettingAsync("fallback_hotkey_enabled", settings.Hotkeys.FallbackHotkeyEnabled ? "true" : "false", cancellationToken);
        await SetSettingAsync("preferred_insertion_mode", settings.Compatibility.PreferredMode.ToString(), cancellationToken);
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

    private static async Task<AppSettings> BuildSettingsAsync(Task<AliyunCredentialSettings> credentialsTask, CancellationToken cancellationToken)
    {
        var credentials = await credentialsTask;
        cancellationToken.ThrowIfCancellationRequested();
        return new AppSettings(
            new RecognitionSettings(
                "阿里云智能语音交互",
                Mask(credentials.AppKey),
                Mask(credentials.AccessKeyId),
                OralSmoothingEnabled: true,
                MicrophoneName: "系统默认麦克风"),
            new HotkeySettings("右 Alt", FallbackHotkeyEnabled: true, "Ctrl + Win + Space"),
            new CompatibilitySettings("尚未捕获", TextInsertionMethod.Auto, false, TextInsertionMethod.Auto),
            new LocalDataSettings(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zumingtalk", "recordings"), 3));
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
