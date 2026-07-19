using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using Zumingtalk.Domain.Services;
using Zumingtalk.Domain.Settings;

namespace Zumingtalk.Infrastructure.Asr;

public sealed class ZumingtalkCloudAsrProvider : IAsrProvider
{
    private readonly ZumingtalkCloudCredentialSettings credentials;
    private readonly bool semanticPunctuationEnabled;

    public ZumingtalkCloudAsrProvider(ZumingtalkCloudCredentialSettings credentials, bool semanticPunctuationEnabled = true)
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
        if (!Uri.TryCreate(credentials.ServiceBaseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("https" or "http") || string.IsNullOrWhiteSpace(credentials.DeviceToken))
        {
            throw new InvalidOperationException("请先完成祖名云端设备激活。");
        }

        using var client = new HttpClient { BaseAddress = baseUri };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credentials.DeviceToken);
        using var response = await client.PostAsync("/api/asr/sessions", null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(response.StatusCode == System.Net.HttpStatusCode.PaymentRequired ? "祖名云端额度不足或已过期。" : "无法创建祖名云端识别会话。");
        }

        var created = await response.Content.ReadFromJsonAsync<CloudSessionResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("祖名云端返回了无效会话。");
        return await CloudAsrSession.StartAsync(created, credentials.DeviceToken, semanticPunctuationEnabled, cancellationToken);
    }

    public async Task<string> RetranscribeAsync(string audioPath, CancellationToken cancellationToken)
    {
        using var reader = new WaveFileReader(audioPath);
        if (reader.WaveFormat.SampleRate != 16000 || reader.WaveFormat.BitsPerSample != 16 || reader.WaveFormat.Channels != 1)
        {
            throw new InvalidOperationException("祖名云端需要 16 kHz、16-bit、单声道 WAV 录音。");
        }

        await using var session = await StartSessionAsync(cancellationToken);
        var buffer = new byte[3200];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            await session.PushAudioAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return await session.FinishAsync(cancellationToken);
    }

    private sealed record CloudSessionResponse(Guid SessionId, int ReservedSeconds, DateTimeOffset ServerTime, string StreamUrl);

    private sealed class CloudAsrSession : IAsrSession
    {
        private readonly ClientWebSocket socket;
        private readonly Guid sessionId;
        private readonly string finishUrl;
        private readonly string deviceToken;
        private readonly CancellationTokenSource receiveCancellation = new();
        private readonly StringBuilder finalText = new();
        private readonly TaskCompletionSource<bool> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task receiveTask;
        private int finishing;
        private int serviceFinished;

        private CloudAsrSession(ClientWebSocket socket, CloudSessionResponse created, string deviceToken)
        {
            this.socket = socket;
            sessionId = created.SessionId;
            var streamUri = new Uri(created.StreamUrl);
            var scheme = streamUri.Scheme == "wss" ? "https" : "http";
            finishUrl = new UriBuilder(scheme, streamUri.Host, streamUri.Port, $"/api/asr/sessions/{created.SessionId:D}/finish").Uri.ToString();
            this.deviceToken = deviceToken;
            receiveTask = ReceiveAsync(receiveCancellation.Token);
        }

        public string? ProviderTaskId => sessionId.ToString("D");

        public static async Task<CloudAsrSession> StartAsync(CloudSessionResponse created, string deviceToken, bool punctuation, CancellationToken cancellationToken)
        {
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", $"Bearer {deviceToken}");
            await socket.ConnectAsync(new Uri(created.StreamUrl), cancellationToken);
            var result = new CloudAsrSession(socket, created, deviceToken);
            var taskId = created.SessionId.ToString("N");
            var payload = BailianFunAsrProvider.BuildRunTaskPayload(taskId, new BailianCredentialSettings("cloud"), punctuation);
            await result.SendJsonAsync(payload, cancellationToken);
            await result.started.Task.WaitAsync(cancellationToken);
            return result;
        }

        public Task PushAudioAsync(ReadOnlyMemory<byte> pcmChunk, CancellationToken cancellationToken) => socket.SendAsync(pcmChunk, WebSocketMessageType.Binary, true, cancellationToken).AsTask();

        public async Task<string> FinishAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (Interlocked.Exchange(ref finishing, 1) == 0)
                {
                    await SendJsonAsync(BailianFunAsrProvider.BuildFinishTaskPayload(sessionId.ToString("N")), cancellationToken);
                }
                var text = await completed.Task.WaitAsync(cancellationToken);
                await FinishOnServiceAsync(true, cancellationToken);
                return text;
            }
            catch
            {
                await FinishOnServiceAsync(false, CancellationToken.None);
                throw;
            }
        }

        public async Task CancelAsync(CancellationToken cancellationToken)
        {
            receiveCancellation.Cancel();
            socket.Abort();
            await FinishOnServiceAsync(false, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            receiveCancellation.Cancel();
            socket.Abort();
            try { await receiveTask; } catch { }
            await FinishOnServiceAsync(false, CancellationToken.None);
            socket.Dispose();
            receiveCancellation.Dispose();
        }

        private async Task FinishOnServiceAsync(bool succeeded, CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref serviceFinished, 1, 0) != 0)
            {
                return;
            }
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);
                using var content = JsonContent.Create(new { succeeded });
                using var response = await client.PostAsync(finishUrl, content, cancellationToken);
                response.EnsureSuccessStatusCode();
            }
            catch
            {
                Interlocked.Exchange(ref serviceFinished, 0);
                throw;
            }
        }

        private Task SendJsonAsync(JsonElement payload, CancellationToken cancellationToken) =>
            socket.SendAsync(Encoding.UTF8.GetBytes(payload.GetRawText()), WebSocketMessageType.Text, true, cancellationToken);

        private async Task ReceiveAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[16 * 1024];
            using var message = new MemoryStream();
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var result = await socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    message.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage) continue;
                    var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
                    using var document = JsonDocument.Parse(json);
                    var root = document.RootElement;
                    var eventName = root.TryGetProperty("header", out var header) && header.TryGetProperty("event", out var eventValue) ? eventValue.GetString() : null;
                    if (eventName == "task-started") started.TrySetResult(true);
                    if (eventName == "result-generated" && root.TryGetProperty("payload", out var payload) && payload.TryGetProperty("output", out var output) && output.TryGetProperty("sentence", out var sentence) && sentence.TryGetProperty("sentence_end", out var ended) && ended.GetBoolean() && sentence.TryGetProperty("text", out var text)) finalText.Append(text.GetString());
                    if (eventName == "task-finished") completed.TrySetResult(finalText.ToString());
                    if (eventName == "task-failed") completed.TrySetException(new InvalidOperationException("祖名云端识别失败。"));
                    message.SetLength(0);
                }

                if (!cancellationToken.IsCancellationRequested && !completed.Task.IsCompleted)
                {
                    var error = new InvalidOperationException("祖名云端会话在完成前关闭。");
                    started.TrySetException(error);
                    completed.TrySetException(error);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception ex) { started.TrySetException(ex); completed.TrySetException(ex); }
        }
    }
}
