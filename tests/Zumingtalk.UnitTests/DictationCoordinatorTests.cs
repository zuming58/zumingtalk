using Zumingtalk.Application.Dictation;
using Zumingtalk.Domain.Dictation;
using Zumingtalk.Domain.Services;
using Zumingtalk.Infrastructure.Asr;
using Zumingtalk.Infrastructure.Windows;

namespace Zumingtalk.UnitTests;

public sealed class DictationCoordinatorTests
{
    [Fact]
    public async Task FinishAsync_SavesHistoryOnly_WhenCapturedTargetIsNone()
    {
        var coordinator = new DictationCoordinator(
            new FakeAudioRecorder(),
            new FakeAsrProvider("没有输入框时仍然保存历史。"),
            new FakeTextInsertionService(InputTargetKind.None));

        await coordinator.StartAsync(CancellationToken.None);
        var result = await coordinator.FinishAsync(CancellationToken.None);

        Assert.Equal(DictationState.Saved, coordinator.State);
        Assert.False(result.Inserted);
        Assert.Equal(InputTargetKind.None, result.CapturedKind);
        Assert.Equal("没有输入框时仍然保存历史。", result.FinalText);
    }

    [Fact]
    public async Task FinishAsync_UsesTextInsertion_WhenCapturedTargetIsEditable()
    {
        var insertion = new FakeTextInsertionService(InputTargetKind.Editable, succeeds: true);
        var coordinator = new DictationCoordinator(
            new FakeAudioRecorder(),
            new FakeAsrProvider("写入文本"),
            insertion);

        await coordinator.StartAsync(CancellationToken.None);
        var result = await coordinator.FinishAsync(CancellationToken.None);

        Assert.Equal(DictationState.Completed, coordinator.State);
        Assert.True(result.Inserted);
        Assert.True(insertion.InsertWasCalled);
    }

    [Fact]
    public async Task Recorder_EmitsPcmChunks_ForRealtimeAsrPipeline()
    {
        var recorder = new FakeAudioRecorder();
        var chunks = new List<byte[]>();
        recorder.PcmAudioAvailable += (_, e) => chunks.Add(e.Buffer);

        await recorder.StartAsync(CancellationToken.None);

        Assert.Single(chunks);
        Assert.Equal([1, 2, 3, 4], chunks[0]);
    }

    [Fact]
    public async Task AliyunProvider_FailsClearly_WhenCredentialsAreMissing()
    {
        var provider = new Infrastructure.Asr.AliyunAsrProvider(new Domain.Settings.AliyunCredentialSettings(string.Empty, string.Empty, string.Empty));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.TestConnectionAsync(CancellationToken.None));

        Assert.Contains("credentials", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MockDataFactory_ProvidesSixConfirmedPrototypeRecords()
    {
        var records = Application.DesignTime.MockDataFactory.CreateRecords();

        Assert.Equal(6, records.Count);
        Assert.True(records[0].IsNewest);
        Assert.Contains("百分之多少", records[0].Text);
    }

    [Fact]
    public async Task AliyunProvider_ReadPcmChunks_SkipsWavRiffHeader()
    {
        using var temp = new TempDirectory();
        var wavPath = Path.Combine(temp.Path, "sample.wav");
        using (var writer = new NAudio.Wave.WaveFileWriter(wavPath, new NAudio.Wave.WaveFormat(16000, 16, 1)))
        {
            writer.Write([1, 2, 3, 4], 0, 4);
        }

        var chunks = new List<AliyunAsrProvider.PcmChunk>();
        await foreach (var chunk in AliyunAsrProvider.ReadPcmChunksAsync(wavPath, CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.Single(chunks);
        Assert.Equal([1, 2, 3, 4], chunks[0].Buffer);
    }

    [Fact]
    public void AliyunTranscriptAggregator_AppendsSentenceEndAndKeepsCurrentInterim()
    {
        var aggregator = new AliyunTranscriptAggregator();

        aggregator.ApplyResult("TranscriptionResultChanged", "第一句");
        aggregator.ApplyResult("SentenceEnd", "第一句。");
        aggregator.ApplyResult("TranscriptionResultChanged", "第二句");

        Assert.Equal("第一句。第二句", aggregator.GetText());
    }

    [Fact]
    public void AliyunTranscriptAggregator_DoesNotOverwriteEarlierSentences()
    {
        var aggregator = new AliyunTranscriptAggregator();

        aggregator.ApplyResult("SentenceEnd", "第一句。");
        aggregator.ApplyResult("SentenceEnd", "第二句。");

        Assert.Equal("第一句。第二句。", aggregator.GetText());
    }

    [Fact]
    public void TextInsertion_UnknownPasteResult_KeepsClipboardFallback()
    {
        var result = WindowsTextInsertionService.EvaluatePasteAttempt(-1, -1, "WM_PASTE");

        Assert.False(result.Verified);
        Assert.True(result.KeepClipboardFallback);
    }

    [Fact]
    public void TextInsertion_LostTarget_DoesNotUseCopyFallback()
    {
        var result = WindowsTextInsertionService.CreateLostTargetResult();

        Assert.False(result.Succeeded);
        Assert.Equal(TextInsertionMethod.Auto, result.Method);
        Assert.Contains("history only", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TranscriptionRecordViewModel_GroupsYesterdaySeparately()
    {
        var record = new TranscriptionRecord(
            Guid.NewGuid(),
            TranscriptionStatus.Completed,
            DateTimeOffset.Now.Date.AddDays(-1).AddHours(9),
            TimeSpan.FromSeconds(1),
            "text",
            null,
            "Aliyun",
            null,
            0,
            4);
        var viewModel = new Application.DesignTime.TranscriptionRecordViewModel(record, false);

        Assert.Equal("昨天", viewModel.DateGroupText);
    }

    [Fact]
    public void TextInsertion_LengthIncrease_IsVerified()
    {
        var result = WindowsTextInsertionService.EvaluatePasteAttempt(4, 8, "WM_PASTE");

        Assert.True(result.Verified);
        Assert.False(result.KeepClipboardFallback);
    }

    [Fact]
    public void TextInsertion_ChromiumClasses_RequireKeyboardPaste()
    {
        Assert.True(WindowsTextInsertionService.RequiresKeyboardPaste("Chrome_WidgetWin_1"));
        Assert.True(WindowsTextInsertionService.RequiresKeyboardPaste("WebViewHost"));
        Assert.True(WindowsTextInsertionService.RequiresKeyboardPaste("HwndWrapper[Zumingtalk]"));
    }

    [Fact]
    public void TextInsertion_KeyboardPasteAttempt_IsNotReportedAsVerifiedInsertion()
    {
        var result = WindowsTextInsertionService.EvaluateKeyboardPasteAttempt(WindowsTextInsertionService.ExpectedCtrlVEventCount);

        Assert.False(result.Succeeded);
        Assert.Equal(TextInsertionMethod.SendInputPaste, result.Method);
        Assert.Contains("attempted", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeAudioRecorder : IAudioRecorder
    {
        public event EventHandler<AudioLevelChangedEventArgs>? LevelChanged;

        public event EventHandler<PcmAudioAvailableEventArgs>? PcmAudioAvailable;

        public Task CancelAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            LevelChanged?.Invoke(this, new AudioLevelChangedEventArgs(0.4));
            PcmAudioAvailable?.Invoke(this, new PcmAudioAvailableEventArgs([1, 2, 3, 4]));
            return Task.CompletedTask;
        }

        public Task<AudioRecordingResult> StopAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AudioRecordingResult("mock.wav", TimeSpan.FromSeconds(3)));
    }

    private sealed class FakeAsrProvider : IAsrProvider
    {
        private readonly string result;

        public FakeAsrProvider(string result)
        {
            this.result = result;
        }

        public Task TestConnectionAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IAsrSession> StartSessionAsync(CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<string> RetranscribeAsync(string audioPath, CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class FakeTextInsertionService : ITextInsertionService
    {
        private readonly InputTargetKind kind;
        private readonly bool succeeds;

        public FakeTextInsertionService(InputTargetKind kind, bool succeeds = false)
        {
            this.kind = kind;
            this.succeeds = succeeds;
        }

        public bool InsertWasCalled { get; private set; }

        public CapturedInputTarget CaptureCurrentTarget() =>
            new(kind, IntPtr.Zero, IntPtr.Zero, 100, "Target", "Medium");

        public CapturedInputTarget ValidateCapturedTarget(CapturedInputTarget capturedTarget) => capturedTarget;

        public Task<TextInsertionResult> InsertAsync(CapturedInputTarget target, string text, CancellationToken cancellationToken)
        {
            InsertWasCalled = true;
            return Task.FromResult(new TextInsertionResult(succeeds, succeeds ? TextInsertionMethod.NativeReplaceSelection : TextInsertionMethod.CopyFallback, "ok"));
        }

        public Task<TextInsertionResult> CopyOnlyAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult(new TextInsertionResult(false, TextInsertionMethod.CopyOnly, "copy"));
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
}
