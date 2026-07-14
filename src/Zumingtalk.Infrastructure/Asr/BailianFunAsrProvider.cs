using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using Zumingtalk.Domain.Services;
using Zumingtalk.Domain.Settings;

namespace Zumingtalk.Infrastructure.Asr;

public sealed class BailianFunAsrProvider : IAsrProvider
{
    private readonly BailianCredentialSettings credentials;
    private readonly bool semanticPunctuationEnabled;

    public BailianFunAsrProvider(BailianCredentialSettings credentials, bool semanticPunctuationEnabled = true)
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
        return await BailianFunAsrSession.StartAsync(credentials, semanticPunctuationEnabled, cancellationToken);
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
            throw new InvalidOperationException("Fun-ASR requires 16 kHz, 16-bit, mono PCM WAV audio.");
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

    internal static JsonElement BuildRunTaskPayload(
        string taskId,
        BailianCredentialSettings credentials,
        bool semanticPunctuationEnabled) =>
        JsonSerializer.SerializeToElement(new
        {
            header = new
            {
                action = "run-task",
                task_id = taskId,
                streaming = "duplex"
            },
            payload = new
            {
                task_group = "audio",
                task = "asr",
                function = "recognition",
                model = credentials.Model,
                parameters = new
                {
                    format = "pcm",
                    sample_rate = 16000,
                    semantic_punctuation_enabled = semanticPunctuationEnabled,
                    heartbeat = true
                },
                input = new { }
            }
        });

    internal static JsonElement BuildFinishTaskPayload(string taskId) =>
        JsonSerializer.SerializeToElement(new
        {
            header = new
            {
                action = "finish-task",
                task_id = taskId,
                streaming = "duplex"
            },
            payload = new { input = new { } }
        });

    private void ValidateCredentials()
    {
        if (string.IsNullOrWhiteSpace(credentials.ApiKey))
        {
            throw new InvalidOperationException("请先在设置页填写阿里云百炼 API Key。");
        }

        if (!Uri.TryCreate(credentials.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "wss")
        {
            throw new InvalidOperationException("百炼 WebSocket 服务地址无效。");
        }

        if (string.IsNullOrWhiteSpace(credentials.Model))
        {
            throw new InvalidOperationException("百炼语音识别模型未配置。");
        }
    }

    private sealed class BailianFunAsrSession : IAsrSession
    {
        private readonly ClientWebSocket socket;
        private readonly CancellationTokenSource receiveCancellation = new();
        private readonly SemaphoreSlim sendLock = new(1, 1);
        private readonly FunAsrTranscriptAggregator transcriptAggregator = new();
        private readonly TaskCompletionSource<bool> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task receiveTask;
        private int finishRequested;

        private BailianFunAsrSession(ClientWebSocket socket, string taskId)
        {
            this.socket = socket;
            ProviderTaskId = taskId;
            receiveTask = ReceiveLoopAsync(receiveCancellation.Token);
        }

        public string? ProviderTaskId { get; }

        public static async Task<BailianFunAsrSession> StartAsync(
            BailianCredentialSettings credentials,
            bool semanticPunctuationEnabled,
            CancellationToken cancellationToken)
        {
            var taskId = Guid.NewGuid().ToString("N");
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", $"Bearer {credentials.ApiKey}");
            socket.Options.SetRequestHeader("User-Agent", "Zumingtalk/0.6");
            BailianFunAsrSession? session = null;

            try
            {
                await socket.ConnectAsync(new Uri(credentials.Endpoint), cancellationToken);
                session = new BailianFunAsrSession(socket, taskId);
                await session.SendJsonAsync(
                    BuildRunTaskPayload(taskId, credentials, semanticPunctuationEnabled),
                    cancellationToken);

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                try
                {
                    await session.started.Task.WaitAsync(linked.Token);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("百炼 Fun-ASR 启动超时，请检查网络和 API Key 地域。");
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

        public async Task PushAudioAsync(ReadOnlyMemory<byte> pcmChunk, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref finishRequested) != 0)
            {
                throw new InvalidOperationException("Fun-ASR session is already finishing.");
            }

            if (socket.State != WebSocketState.Open)
            {
                throw new InvalidOperationException("Fun-ASR WebSocket connection is not open.");
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
            if (Interlocked.Exchange(ref finishRequested, 1) != 0)
            {
                return await completed.Task.WaitAsync(cancellationToken);
            }

            await SendJsonAsync(BuildFinishTaskPayload(ProviderTaskId!), cancellationToken);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                return await completed.Task.WaitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("百炼 Fun-ASR 完成任务超时，录音会保留并自动重试。");
            }
        }

        public Task CancelAsync(CancellationToken cancellationToken)
        {
            receiveCancellation.Cancel();
            socket.Abort();
            started.TrySetCanceled(cancellationToken);
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
                // Receive-loop failures are surfaced through the session tasks.
            }

            receiveCancellation.Dispose();
            sendLock.Dispose();
            socket.Dispose();
        }

        private async Task SendJsonAsync(object payload, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
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
            using var messageBuffer = new MemoryStream();
            try
            {
                while (!cancellationToken.IsCancellationRequested &&
                       socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    var result = await socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        messageBuffer.SetLength(0);
                        continue;
                    }

                    messageBuffer.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage)
                    {
                        continue;
                    }

                    HandleEvent(Encoding.UTF8.GetString(messageBuffer.GetBuffer(), 0, checked((int)messageBuffer.Length)));
                    messageBuffer.SetLength(0);
                }

                if (!cancellationToken.IsCancellationRequested && !completed.Task.IsCompleted)
                {
                    var error = new InvalidOperationException("百炼语音识别连接在任务完成前已关闭。");
                    started.TrySetException(error);
                    completed.TrySetException(error);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                started.TrySetException(ex);
                completed.TrySetException(ex);
            }
        }

        private void HandleEvent(string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("header", out var header) ||
                !header.TryGetProperty("event", out var eventElement))
            {
                return;
            }

            var eventName = eventElement.GetString();
            switch (eventName)
            {
                case "task-started":
                    started.TrySetResult(true);
                    break;

                case "result-generated":
                    ApplyResult(root);
                    break;

                case "task-finished":
                    completed.TrySetResult(transcriptAggregator.GetText());
                    break;

                case "task-failed":
                    var code = header.TryGetProperty("error_code", out var codeElement)
                        ? codeElement.GetString()
                        : "UNKNOWN_ERROR";
                    var message = header.TryGetProperty("error_message", out var messageElement)
                        ? messageElement.GetString()
                        : "百炼 Fun-ASR 任务失败。";
                    var error = new InvalidOperationException($"{code}: {message}");
                    started.TrySetException(error);
                    completed.TrySetException(error);
                    break;
            }
        }

        private void ApplyResult(JsonElement root)
        {
            if (!root.TryGetProperty("payload", out var payload) ||
                !payload.TryGetProperty("output", out var output) ||
                !output.TryGetProperty("sentence", out var sentence))
            {
                return;
            }

            var heartbeat = sentence.TryGetProperty("heartbeat", out var heartbeatElement) && heartbeatElement.GetBoolean();
            var sentenceEnd = sentence.TryGetProperty("sentence_end", out var sentenceEndElement) && sentenceEndElement.GetBoolean();
            var sentenceId = sentence.TryGetProperty("sentence_id", out var sentenceIdElement) ? sentenceIdElement.GetInt32() : 0;
            var text = sentence.TryGetProperty("text", out var textElement) ? textElement.GetString() ?? string.Empty : string.Empty;
            transcriptAggregator.ApplyResult(sentenceId, text, sentenceEnd, heartbeat);
        }
    }
}

internal sealed class FunAsrTranscriptAggregator
{
    private readonly SortedDictionary<int, string> finalizedSentences = [];
    private int interimSentenceId;
    private string interimText = string.Empty;

    public void ApplyResult(int sentenceId, string text, bool sentenceEnd, bool heartbeat)
    {
        if (heartbeat || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (sentenceEnd)
        {
            finalizedSentences[sentenceId] = text;
            if (interimSentenceId == sentenceId)
            {
                interimSentenceId = 0;
                interimText = string.Empty;
            }

            return;
        }

        interimSentenceId = sentenceId;
        interimText = text;
    }

    public string GetText()
    {
        var builder = new StringBuilder();
        foreach (var sentence in finalizedSentences.Values)
        {
            builder.Append(sentence);
        }

        if (!string.IsNullOrWhiteSpace(interimText) && !finalizedSentences.ContainsKey(interimSentenceId))
        {
            builder.Append(interimText);
        }

        return builder.ToString();
    }
}
