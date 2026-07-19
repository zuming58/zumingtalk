using System.IO.Compression;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using Zumingtalk.Domain.Services;
using Zumingtalk.Domain.Settings;

namespace Zumingtalk.Infrastructure.Asr;

/// <summary>
/// Direct client for Volcengine's documented bigmodel_async WebSocket ASR protocol.
/// User credentials stay local and are never sent to the Zumingtalk service.
/// </summary>
public sealed class VolcengineBigModelAsrProvider : IAsrProvider
{
    private readonly VolcengineCredentialSettings credentials;
    private readonly bool semanticPunctuationEnabled;

    public VolcengineBigModelAsrProvider(VolcengineCredentialSettings credentials, bool semanticPunctuationEnabled = true)
    {
        this.credentials = credentials;
        this.semanticPunctuationEnabled = semanticPunctuationEnabled;
    }

    public async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        await using var session = await StartSessionAsync(cancellationToken);
        await session.CancelAsync(cancellationToken);
    }

    public async Task<IAsrSession> StartSessionAsync(CancellationToken cancellationToken)
    {
        ValidateCredentials();
        return await VolcengineBigModelAsrSession.StartAsync(credentials, semanticPunctuationEnabled, cancellationToken);
    }

    public async Task<string> RetranscribeAsync(string audioPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(audioPath))
        {
            throw new FileNotFoundException("Audio file was not found.", audioPath);
        }

        await using var session = await StartSessionAsync(cancellationToken);
        await foreach (var chunk in BailianFunAsrProvider.ReadPcmChunksAsync(audioPath, cancellationToken))
        {
            await session.PushAudioAsync(chunk.Buffer, cancellationToken);
            await Task.Delay(chunk.Duration, cancellationToken);
        }

        return await session.FinishAsync(cancellationToken);
    }

    internal static byte[] BuildFullClientRequestFrame(bool semanticPunctuationEnabled) =>
        BuildFrame(0x10, 0x11, JsonSerializer.SerializeToUtf8Bytes(new
        {
            user = new
            {
                uid = "zumingtalk",
                platform = "Windows",
                sdk_version = "Zumingtalk",
                app_version = typeof(VolcengineBigModelAsrProvider).Assembly.GetName().Version?.ToString()
            },
            audio = new { format = "pcm", codec = "raw", rate = 16000, bits = 16, channel = 1 },
            request = new
            {
                model_name = "bigmodel",
                enable_itn = true,
                enable_punc = semanticPunctuationEnabled,
                // Keep transcription faithful. Semantic smoothing changes spoken content.
                enable_ddc = false,
                show_utterances = true
            }
        }));

    internal static byte[] BuildAudioFrame(ReadOnlySpan<byte> pcmChunk, bool isLast) =>
        BuildFrame(isLast ? (byte)0x22 : (byte)0x20, 0x01, pcmChunk);

    internal static byte[] BuildFrame(byte typeAndFlags, byte serializationAndCompression, ReadOnlySpan<byte> payload)
    {
        var compressed = Compress(payload);
        var frame = new byte[8 + compressed.Length];
        frame[0] = 0x11;
        frame[1] = typeAndFlags;
        frame[2] = serializationAndCompression;
        frame[3] = 0;
        WriteInt32BigEndian(frame.AsSpan(4, 4), compressed.Length);
        compressed.CopyTo(frame.AsSpan(8));
        return frame;
    }

    private void ValidateCredentials()
    {
        if (string.IsNullOrWhiteSpace(credentials.ApiKey))
        {
            throw new InvalidOperationException("请先在设置页填写火山引擎 API Key。");
        }

        if (string.IsNullOrWhiteSpace(credentials.ResourceId))
        {
            throw new InvalidOperationException("请填写火山引擎 Resource ID。");
        }

        if (!Uri.TryCreate(credentials.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "wss")
        {
            throw new InvalidOperationException("火山引擎 WebSocket 服务地址无效。");
        }
    }

    private static byte[] Compress(ReadOnlySpan<byte> payload)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(payload);
        }

        return output.ToArray();
    }

    private static byte[] Decompress(ReadOnlySpan<byte> payload)
    {
        using var input = new MemoryStream(payload.ToArray());
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static void WriteInt32BigEndian(Span<byte> destination, int value)
    {
        destination[0] = (byte)(value >> 24);
        destination[1] = (byte)(value >> 16);
        destination[2] = (byte)(value >> 8);
        destination[3] = (byte)value;
    }

    private static int ReadInt32BigEndian(ReadOnlySpan<byte> source) =>
        (source[0] << 24) | (source[1] << 16) | (source[2] << 8) | source[3];

    private sealed class VolcengineBigModelAsrSession : IAsrSession
    {
        private readonly ClientWebSocket socket;
        private readonly CancellationTokenSource receiveCancellation = new();
        private readonly SemaphoreSlim sendLock = new(1, 1);
        private readonly TaskCompletionSource<bool> initialResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task receiveTask;
        private string transcript = string.Empty;
        private int finishRequested;

        private VolcengineBigModelAsrSession(ClientWebSocket socket, string requestId)
        {
            this.socket = socket;
            ProviderTaskId = requestId;
            receiveTask = ReceiveLoopAsync(receiveCancellation.Token);
        }

        public string? ProviderTaskId { get; }

        public static async Task<VolcengineBigModelAsrSession> StartAsync(
            VolcengineCredentialSettings credentials,
            bool semanticPunctuationEnabled,
            CancellationToken cancellationToken)
        {
            var requestId = Guid.NewGuid().ToString();
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("X-Api-Key", credentials.ApiKey);
            socket.Options.SetRequestHeader("X-Api-Resource-Id", credentials.ResourceId);
            socket.Options.SetRequestHeader("X-Api-Request-Id", requestId);
            socket.Options.SetRequestHeader("X-Api-Connect-Id", Guid.NewGuid().ToString());
            socket.Options.SetRequestHeader("X-Api-Sequence", "-1");
            socket.Options.SetRequestHeader("User-Agent", "Zumingtalk/0.7");
            VolcengineBigModelAsrSession? session = null;

            try
            {
                await socket.ConnectAsync(new Uri(credentials.Endpoint), cancellationToken);
                session = new VolcengineBigModelAsrSession(socket, requestId);
                await session.SendFrameAsync(BuildFullClientRequestFrame(semanticPunctuationEnabled), cancellationToken);

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                try
                {
                    await session.initialResponse.Task.WaitAsync(linked.Token);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("火山引擎 ASR 启动超时，请检查网络、API Key 和 Resource ID。");
                }

                return session;
            }
            catch
            {
                if (session is not null)
                {
                    await session.DisposeAsync();
                }
                else
                {
                    socket.Dispose();
                }

                throw;
            }
        }

        public Task PushAudioAsync(ReadOnlyMemory<byte> pcmChunk, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref finishRequested) != 0)
            {
                throw new InvalidOperationException("Volcengine ASR session is already finishing.");
            }

            return SendFrameAsync(BuildAudioFrame(pcmChunk.Span, isLast: false), cancellationToken);
        }

        public async Task<string> FinishAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref finishRequested, 1) == 0)
            {
                await SendFrameAsync(BuildAudioFrame([], isLast: true), cancellationToken);
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                return await completed.Task.WaitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("火山引擎 ASR 完成超时，录音会保留并可重新转写。");
            }
        }

        public Task CancelAsync(CancellationToken cancellationToken)
        {
            receiveCancellation.Cancel();
            socket.Abort();
            initialResponse.TrySetCanceled(cancellationToken);
            completed.TrySetCanceled(cancellationToken);
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            receiveCancellation.Cancel();
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived or WebSocketState.CloseSent)
            {
                socket.Abort();
            }

            try
            {
                await receiveTask;
            }
            catch
            {
                // Receive-loop failures are exposed through the session completion tasks.
            }

            receiveCancellation.Dispose();
            sendLock.Dispose();
            socket.Dispose();
        }

        private async Task SendFrameAsync(byte[] frame, CancellationToken cancellationToken)
        {
            if (socket.State != WebSocketState.Open)
            {
                throw new InvalidOperationException("Volcengine ASR WebSocket connection is not open.");
            }

            await sendLock.WaitAsync(cancellationToken);
            try
            {
                await socket.SendAsync(frame, WebSocketMessageType.Binary, true, cancellationToken);
            }
            finally
            {
                sendLock.Release();
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[16 * 1024];
            using var messageBuffer = new MemoryStream();
            try
            {
                while (!cancellationToken.IsCancellationRequested && socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    var result = await socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    if (result.MessageType != WebSocketMessageType.Binary)
                    {
                        continue;
                    }

                    messageBuffer.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage)
                    {
                        continue;
                    }

                    HandleFrame(messageBuffer.GetBuffer().AsSpan(0, checked((int)messageBuffer.Length)));
                    messageBuffer.SetLength(0);
                }

                if (!cancellationToken.IsCancellationRequested && !completed.Task.IsCompleted)
                {
                    var error = new InvalidOperationException("火山引擎语音识别连接在任务完成前已关闭。");
                    initialResponse.TrySetException(error);
                    completed.TrySetException(error);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                initialResponse.TrySetException(ex);
                completed.TrySetException(ex);
            }
        }

        private void HandleFrame(ReadOnlySpan<byte> frame)
        {
            if (frame.Length < 8 || frame[0] >> 4 != 1)
            {
                throw new InvalidOperationException("火山引擎返回了无效的 ASR 帧。");
            }

            var headerLength = (frame[0] & 0x0f) * 4;
            if (headerLength < 4 || frame.Length < headerLength + 4)
            {
                throw new InvalidOperationException("火山引擎返回的 ASR 帧不完整。");
            }

            var messageType = (byte)(frame[1] >> 4);
            var flags = (byte)(frame[1] & 0x0f);
            var compression = (byte)(frame[2] & 0x0f);
            var offset = headerLength;

            if (messageType == 15)
            {
                if (frame.Length < offset + 8)
                {
                    throw new InvalidOperationException("火山引擎返回的错误帧不完整。");
                }

                var errorCode = ReadInt32BigEndian(frame.Slice(offset, 4));
                offset += 4;
                var errorPayload = ReadPayload(frame, offset, compression);
                throw new InvalidOperationException($"火山引擎 ASR 错误 {errorCode}: {ReadErrorMessage(errorPayload)}");
            }

            if (messageType != 9)
            {
                return;
            }

            if (flags is 1 or 3)
            {
                if (frame.Length < offset + 4)
                {
                    throw new InvalidOperationException("火山引擎返回的响应序号不完整。");
                }

                offset += 4;
            }

            var payload = ReadPayload(frame, offset, compression);
            ApplyResult(payload);
            initialResponse.TrySetResult(true);
            if (flags == 3)
            {
                completed.TrySetResult(transcript);
            }
        }

        private static byte[] ReadPayload(ReadOnlySpan<byte> frame, int offset, byte compression)
        {
            if (frame.Length < offset + 4)
            {
                throw new InvalidOperationException("火山引擎响应缺少负载长度。");
            }

            var length = ReadInt32BigEndian(frame.Slice(offset, 4));
            offset += 4;
            if (length < 0 || frame.Length < offset + length)
            {
                throw new InvalidOperationException("火山引擎响应负载长度无效。");
            }

            var payload = frame.Slice(offset, length);
            return compression switch
            {
                0 => payload.ToArray(),
                1 => Decompress(payload),
                _ => throw new InvalidOperationException("火山引擎返回了不支持的压缩格式。")
            };
        }

        private void ApplyResult(byte[] payload)
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!root.TryGetProperty("result", out var result))
            {
                return;
            }

            var text = result.ValueKind switch
            {
                JsonValueKind.Object when result.TryGetProperty("text", out var textElement) => textElement.GetString(),
                JsonValueKind.Array when result.GetArrayLength() > 0 && result[0].TryGetProperty("text", out var textElement) => textElement.GetString(),
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(text))
            {
                transcript = text;
            }
        }

        private static string ReadErrorMessage(byte[] payload)
        {
            try
            {
                using var document = JsonDocument.Parse(payload);
                return document.RootElement.TryGetProperty("message", out var message)
                    ? message.GetString() ?? "Unknown error"
                    : "Unknown error";
            }
            catch (JsonException)
            {
                return Encoding.UTF8.GetString(payload);
            }
        }
    }
}
