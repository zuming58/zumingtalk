namespace Zumingtalk.Domain.Services;

public interface IAsrProvider
{
    Task TestConnectionAsync(CancellationToken cancellationToken);

    Task<IAsrSession> StartSessionAsync(CancellationToken cancellationToken);

    Task<string> RetranscribeAsync(string audioPath, CancellationToken cancellationToken);
}

public interface IAsrSession : IAsyncDisposable
{
    string? ProviderTaskId { get; }

    Task PushAudioAsync(ReadOnlyMemory<byte> pcmChunk, CancellationToken cancellationToken);

    Task<string> FinishAsync(CancellationToken cancellationToken);

    Task CancelAsync(CancellationToken cancellationToken);
}

public interface ICloudAccountClient
{
    Task ActivateAsync(string serviceBaseUrl, string inviteCode, CancellationToken cancellationToken);

    Task<CloudEntitlementSnapshot> GetEntitlementAsync(CancellationToken cancellationToken);
}

public interface IDeviceFingerprintProvider
{
    Task<string> GetOrCreateAsync(CancellationToken cancellationToken);
}

public sealed record CloudEntitlementSnapshot(string Plan, DateTimeOffset ServerTime, IReadOnlyList<CloudQuotaBucket> QuotaBuckets);

public sealed record CloudQuotaBucket(string Kind, int RemainingSeconds, DateTimeOffset? ExpiresAt);
