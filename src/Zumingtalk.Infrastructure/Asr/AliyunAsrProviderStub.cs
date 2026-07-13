using Zumingtalk.Domain.Services;

namespace Zumingtalk.Infrastructure.Asr;

public sealed class AliyunAsrProviderStub : IAsrProvider
{
    public Task TestConnectionAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IAsrSession> StartSessionAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IAsrSession>(new StubSession());

    public Task<string> RetranscribeAsync(string audioPath, CancellationToken cancellationToken) =>
        Task.FromResult("这是一段来自阿里云实时识别适配器占位实现的模拟结果。");

    private sealed class StubSession : IAsrSession
    {
        public string? ProviderTaskId => "m1-stub-session";

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task PushAudioAsync(ReadOnlyMemory<byte> pcmChunk, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string> FinishAsync(CancellationToken cancellationToken) =>
            Task.FromResult("这是一段来自阿里云实时识别适配器占位实现的模拟结果。");

        public Task CancelAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
