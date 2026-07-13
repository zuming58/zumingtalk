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
