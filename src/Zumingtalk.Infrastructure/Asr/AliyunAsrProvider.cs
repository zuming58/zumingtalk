using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using Zumingtalk.Domain.Services;
using Zumingtalk.Domain.Settings;

namespace Zumingtalk.Infrastructure.Asr;

public sealed class AliyunAsrProvider : IAsrProvider
{
    private readonly AliyunCredentialSettings credentials;
    private readonly AliyunTokenProvider tokenProvider;

    public AliyunAsrProvider(AliyunCredentialSettings credentials)
        : this(credentials, new AliyunTokenProvider(credentials))
    {
    }

    public AliyunAsrProvider(AliyunCredentialSettings credentials, AliyunTokenProvider tokenProvider)
    {
        this.credentials = credentials;
        this.tokenProvider = tokenProvider;
    }

    public async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        _ = await tokenProvider.GetTokenAsync(forceRefresh: true, cancellationToken);
    }

    public async Task<IAsrSession> StartSessionAsync(CancellationToken cancellationToken)
    {
        var token = await tokenProvider.GetTokenAsync(forceRefresh: false, cancellationToken);
        return await AliyunAsrSession.StartAsync(credentials, token, cancellationToken);
    }

    public async Task<string> RetranscribeAsync(string audioPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(audioPath))
        {
            throw new FileNotFoundException("Audio file was not found.", audioPath);
        }

        await using var session = await StartSessionAsync(cancellationToken);
        await foreach (var chunk in ReadPcmChunksAsync(audioPath, cancellationToken))
        {
            await session.PushAudioAsync(chunk.Buffer, cancellationToken);
            await Task.Delay(chunk.Duration, cancellationToken);
        }

        return await session.FinishAsync(cancellationToken);
    }

    internal static async IAsyncEnumerable<PcmChunk> ReadPcmChunksAsync(
        string audioPath,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new WaveFileReader(audioPath);
        if (reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm ||
            reader.WaveFormat.SampleRate != 16000 ||
            reader.WaveFormat.BitsPerSample != 16 ||
            reader.WaveFormat.Channels != 1)
        {
            throw new InvalidOperationException("Aliyun realtime retranscription requires 16 kHz, 16-bit, mono PCM WAV audio.");
        }

        var buffer = new byte[3200];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
            {
                yield break;
            }

            var chunk = new byte[read];
            Buffer.BlockCopy(buffer, 0, chunk, 0, read);
            yield return new PcmChunk(chunk, TimeSpan.FromSeconds((double)read / reader.WaveFormat.AverageBytesPerSecond));
        }
    }

    internal sealed record PcmChunk(byte[] Buffer, TimeSpan Duration);

    private sealed class AliyunAsrSession : IAsrSession
    {
        private readonly ClientWebSocket socket;
        private readonly CancellationTokenSource receiveCancellation = new();
        private readonly SemaphoreSlim sendLock = new(1, 1);
        private readonly Task receiveTask;
        private readonly object sync = new();
        private string finalText = string.Empty;
        private TaskCompletionSource<string> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private AliyunAsrSession(ClientWebSocket socket, string taskId)
        {
            this.socket = socket;
            ProviderTaskId = taskId;
            receiveTask = ReceiveLoopAsync(receiveCancellation.Token);
        }

        public string? ProviderTaskId { get; }

        public static async Task<AliyunAsrSession> StartAsync(AliyunCredentialSettings credentials, string token, CancellationToken cancellationToken)
        {
            var taskId = Guid.NewGuid().ToString("N");
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("X-NLS-Token", token);
            await socket.ConnectAsync(new Uri($"{credentials.Endpoint}?token={Uri.EscapeDataString(token)}"), cancellationToken);
            var session = new AliyunAsrSession(socket, taskId);
            await session.SendJsonAsync(new
            {
                header = new
                {
                    appkey = credentials.AppKey,
                    message_id = Guid.NewGuid().ToString("N"),
                    task_id = taskId,
                    @namespace = "SpeechTranscriber",
                    name = "StartTranscription"
                },
                payload = new
                {
                    format = "pcm",
                    sample_rate = 16000,
                    enable_intermediate_result = true,
                    enable_punctuation_prediction = true,
                    enable_inverse_text_normalization = true,
                    enable_semantic_sentence_detection = true
                }
            }, cancellationToken);
            return session;
        }

        public async Task PushAudioAsync(ReadOnlyMemory<byte> pcmChunk, CancellationToken cancellationToken)
        {
            if (socket.State != WebSocketState.Open)
            {
                throw new InvalidOperationException("Aliyun ASR socket is not open.");
            }

            await sendLock.WaitAsync(cancellationToken);
            try
            {
                await socket.SendAsync(pcmChunk, WebSocketMessageType.Binary, true, cancellationToken);
            }
            finally
            {
                sendLock.Release();
            }
        }

        public async Task<string> FinishAsync(CancellationToken cancellationToken)
        {
            await SendJsonAsync(new
            {
                header = new
                {
                    message_id = Guid.NewGuid().ToString("N"),
                    task_id = ProviderTaskId,
                    @namespace = "SpeechTranscriber",
                    name = "StopTranscription"
                }
            }, cancellationToken);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            using var registration = linked.Token.Register(() => completed.TrySetCanceled(linked.Token));
            return await completed.Task;
        }

        public async Task CancelAsync(CancellationToken cancellationToken)
        {
            receiveCancellation.Cancel();
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "cancel", cancellationToken);
            }
        }

        public async ValueTask DisposeAsync()
        {
            receiveCancellation.Cancel();
            try
            {
                await receiveTask;
            }
            catch
            {
                // Ignore receive-loop shutdown errors during disposal.
            }

            receiveCancellation.Dispose();
            sendLock.Dispose();
            socket.Dispose();
        }

        private async Task SendJsonAsync(object payload, CancellationToken cancellationToken)
        {
            var json = JsonSerializer.Serialize(payload);
            var bytes = Encoding.UTF8.GetBytes(json);
            await sendLock.WaitAsync(cancellationToken);
            try
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
            }
            finally
            {
                sendLock.Release();
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[16 * 1024];
            var builder = new StringBuilder();
            try
            {
                while (!cancellationToken.IsCancellationRequested && socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    var result = await socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        continue;
                    }

                    builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (!result.EndOfMessage)
                    {
                        continue;
                    }

                    HandleEvent(builder.ToString());
                    builder.Clear();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                completed.TrySetException(ex);
            }
        }

        private void HandleEvent(string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var name = root.TryGetProperty("header", out var header) && header.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString()
                : null;

            if (string.Equals(name, "SentenceEnd", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "TranscriptionResultChanged", StringComparison.OrdinalIgnoreCase))
            {
                if (root.TryGetProperty("payload", out var payload) && payload.TryGetProperty("result", out var result))
                {
                    lock (sync)
                    {
                        finalText = result.GetString() ?? finalText;
                    }
                }
            }

            if (string.Equals(name, "TranscriptionCompleted", StringComparison.OrdinalIgnoreCase))
            {
                lock (sync)
                {
                    completed.TrySetResult(finalText);
                }
            }

            if (string.Equals(name, "TaskFailed", StringComparison.OrdinalIgnoreCase))
            {
                var message = root.TryGetProperty("header", out var failedHeader) &&
                    failedHeader.TryGetProperty("status_text", out var statusText)
                        ? statusText.GetString()
                        : "Aliyun ASR task failed.";
                completed.TrySetException(new InvalidOperationException(message));
            }
        }
    }
}
