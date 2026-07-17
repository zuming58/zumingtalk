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
    public void Recorder_PeakCalculation_HandlesMinimumPcmSample()
    {
        var buffer = BitConverter.GetBytes(short.MinValue);

        var peak = Infrastructure.Audio.NAudioRecorder.CalculatePeak(buffer);

        Assert.Equal(1d, peak);
    }

    [Fact]
    public void AudioLevelMeter_ReportsSilence_ForZeroPcm()
    {
        var meter = new Infrastructure.Audio.AudioLevelMeter();
        var silence = new byte[1600];

        var level = meter.ProcessPcm16Mono(silence, 16000);

        Assert.Equal(0d, level);
    }

    [Fact]
    public void AudioLevelMeter_HandlesMinimumSampleWithoutOverflow()
    {
        var dbfs = Infrastructure.Audio.AudioLevelMeter.CalculateDbfs(BitConverter.GetBytes(short.MinValue));

        Assert.True(double.IsFinite(dbfs));
        Assert.InRange(dbfs, -0.001, 0.001);
    }

    [Fact]
    public void AudioLevelMeter_DropsToSilence_AfterHysteresisWindow()
    {
        var meter = new Infrastructure.Audio.AudioLevelMeter();
        var speech = Enumerable.Range(0, 800).SelectMany(_ => BitConverter.GetBytes((short)9000)).ToArray();
        var quiet = Enumerable.Range(0, 4000).SelectMany(_ => BitConverter.GetBytes((short)8)).ToArray();

        var speechLevel = meter.ProcessPcm16Mono(speech, 16000);
        var quietLevel = meter.ProcessPcm16Mono(quiet, 16000);

        Assert.True(speechLevel > 0);
        Assert.Equal(0d, quietLevel);
    }

    [Fact]
    public async Task BailianProvider_FailsClearly_WhenApiKeyIsMissing()
    {
        var provider = new BailianFunAsrProvider(new Domain.Settings.BailianCredentialSettings(string.Empty));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.TestConnectionAsync(CancellationToken.None));

        Assert.Contains("API Key", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BailianProvider_BuildsOfficialFunAsrRealtimeProtocolPayloads()
    {
        const string taskId = "0123456789abcdef0123456789abcdef";
        var credentials = new Domain.Settings.BailianCredentialSettings("sk-test");

        var runTask = BailianFunAsrProvider.BuildRunTaskPayload(taskId, credentials, semanticPunctuationEnabled: true);
        var finishTask = BailianFunAsrProvider.BuildFinishTaskPayload(taskId);

        Assert.Equal("run-task", runTask.GetProperty("header").GetProperty("action").GetString());
        Assert.Equal("duplex", runTask.GetProperty("header").GetProperty("streaming").GetString());
        Assert.Equal("fun-asr-realtime", runTask.GetProperty("payload").GetProperty("model").GetString());
        var parameters = runTask.GetProperty("payload").GetProperty("parameters");
        Assert.Equal("pcm", parameters.GetProperty("format").GetString());
        Assert.Equal(16000, parameters.GetProperty("sample_rate").GetInt32());
        Assert.True(parameters.GetProperty("semantic_punctuation_enabled").GetBoolean());
        Assert.True(parameters.GetProperty("heartbeat").GetBoolean());
        Assert.Equal("finish-task", finishTask.GetProperty("header").GetProperty("action").GetString());
        Assert.Equal(taskId, finishTask.GetProperty("header").GetProperty("task_id").GetString());
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
    public async Task BailianProvider_ReadPcmChunks_SkipsWavRiffHeader()
    {
        using var temp = new TempDirectory();
        var wavPath = Path.Combine(temp.Path, "sample.wav");
        using (var writer = new NAudio.Wave.WaveFileWriter(wavPath, new NAudio.Wave.WaveFormat(16000, 16, 1)))
        {
            writer.Write([1, 2, 3, 4], 0, 4);
        }

        var chunks = new List<BailianFunAsrProvider.PcmChunk>();
        await foreach (var chunk in BailianFunAsrProvider.ReadPcmChunksAsync(wavPath, CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.Single(chunks);
        Assert.Equal([1, 2, 3, 4], chunks[0].Buffer);
    }

    [Fact]
    public void FunAsrTranscriptAggregator_AppendsFinalSentenceAndKeepsCurrentInterim()
    {
        var aggregator = new FunAsrTranscriptAggregator();

        aggregator.ApplyResult(1, "第一句", sentenceEnd: false, heartbeat: false);
        aggregator.ApplyResult(1, "第一句。", sentenceEnd: true, heartbeat: false);
        aggregator.ApplyResult(2, "第二句", sentenceEnd: false, heartbeat: false);

        Assert.Equal("第一句。第二句", aggregator.GetText());
    }

    [Fact]
    public void FunAsrTranscriptAggregator_OrdersFinalSentencesAndIgnoresHeartbeat()
    {
        var aggregator = new FunAsrTranscriptAggregator();

        aggregator.ApplyResult(2, "第二句。", sentenceEnd: true, heartbeat: false);
        aggregator.ApplyResult(0, "忽略", sentenceEnd: true, heartbeat: true);
        aggregator.ApplyResult(1, "第一句。", sentenceEnd: true, heartbeat: false);

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
    public void TextInsertion_KeyboardPasteAttempt_IsNotReportedAsVerifiedInsertion()
    {
        var result = WindowsTextInsertionService.EvaluateKeyboardPasteAttempt(WindowsTextInsertionService.ExpectedCtrlVEventCount);

        Assert.False(result.Succeeded);
        Assert.Equal(TextInsertionMethod.SendInputPaste, result.Method);
        Assert.Contains("attempted", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TextInsertion_ChromiumClasses_RequireKeyboardPaste()
    {
        Assert.True(WindowsTextInsertionService.RequiresKeyboardPaste("Chrome_WidgetWin_1"));
        Assert.True(WindowsTextInsertionService.RequiresKeyboardPaste("WebViewHost"));
        Assert.True(WindowsTextInsertionService.RequiresKeyboardPaste("HwndWrapper[Codex]"));
        Assert.False(WindowsTextInsertionService.RequiresKeyboardPaste("Edit"));
    }

    [Fact]
    public void TextInsertion_SendInputStructure_MatchesNativePlatformSize()
    {
        Assert.Equal(IntPtr.Size == 8 ? 40 : 28, WindowsTextInsertionService.NativeInputStructureSize);
    }

    [Theory]
    [InlineData("Weixin", "Qt51514QWindowIcon", true)]
    [InlineData("WeChat", "Qt51514QWindowIcon", true)]
    [InlineData("WXWork", "Qt6QWindowIcon", true)]
    [InlineData("OtherQtApp", "Qt51514QWindowIcon", false)]
    [InlineData("Weixin", "Chrome_WidgetWin_1", false)]
    public void TextInsertion_RecognizesKnownWeChatQtTargets(string processName, string className, bool expected)
    {
        Assert.Equal(expected, WindowsTextInsertionService.IsKnownQtKeyboardPasteTarget(processName, className));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    public void TextInsertion_OnlyBlocksHigherIntegrityTargets(
        bool currentProcessElevated,
        bool targetProcessElevated,
        bool expected)
    {
        Assert.Equal(expected, WindowsTextInsertionService.CanInjectIntoTarget(currentProcessElevated, targetProcessElevated));
    }

    [Fact]
    public void TextInsertion_KeyboardPaste_KeepsCopyFallback_WhenInputWasBlocked()
    {
        var result = WindowsTextInsertionService.EvaluateKeyboardPasteAttempt(0);

        Assert.False(result.Succeeded);
        Assert.Equal(TextInsertionMethod.CopyFallback, result.Method);
    }

    [Fact]
    public void TextInsertion_KeyboardPaste_DiagnosticsKeepClipboardFallback()
    {
        var diagnostics = new InputTargetDiagnostics(
            "Chrome_WidgetWin_1",
            "Chrome_RenderWidgetHostHWND",
            IntPtr.Zero,
            "ControlType.Edit",
            "Chrome",
            string.Empty,
            true,
            true,
            true,
            true,
            true,
            "Released");

        var result = WindowsTextInsertionService.EvaluateKeyboardPasteAttempt(
            new WindowsTextInsertionService.KeyboardPasteAttemptResult(
                WindowsTextInsertionService.ExpectedCtrlVEventCount,
                0,
                true),
            diagnostics);

        Assert.False(result.Succeeded);
        Assert.Equal(TextInsertionMethod.SendInputPaste, result.Method);
        Assert.NotNull(result.Diagnostics);
        Assert.True(result.Diagnostics.KeepsClipboardFallback);
        Assert.Equal((uint)WindowsTextInsertionService.ExpectedCtrlVEventCount, result.Diagnostics.SendInputEvents);
    }

    [Fact]
    public void TextInsertion_KeyboardPaste_IsSuccessful_WhenAutomationVerifiesTextChange()
    {
        var result = WindowsTextInsertionService.EvaluateKeyboardPasteAttempt(
            new WindowsTextInsertionService.KeyboardPasteAttemptResult(
                WindowsTextInsertionService.ExpectedCtrlVEventCount,
                0,
                true,
                true,
                true));

        Assert.True(result.Succeeded);
        Assert.Equal(TextInsertionMethod.SendInputPaste, result.Method);
        Assert.Contains("verified", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TextInsertion_KeyboardPaste_CancelsWhenTargetChanged()
    {
        var result = WindowsTextInsertionService.EvaluateKeyboardPasteAttempt(
            new WindowsTextInsertionService.KeyboardPasteAttemptResult(
                0,
                0,
                true,
                false));

        Assert.False(result.Succeeded);
        Assert.Equal(TextInsertionMethod.CopyFallback, result.Method);
        Assert.Contains("Target changed", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Edit", false, false, true)]
    [InlineData("Chrome_RenderWidgetHostHWND", false, true, true)]
    [InlineData("Chrome_RenderWidgetHostHWND", true, false, true)]
    [InlineData("Chrome_WidgetWin_1", false, false, false)]
    [InlineData("Qt5152QWindowIcon", false, false, false)]
    public void TextInsertion_OnlyTreatsSafeTargetsAsEditable(
        string className,
        bool hasCaret,
        bool automationCandidate,
        bool expected)
    {
        Assert.Equal(expected, WindowsTextInsertionService.IsSafeEditableTarget(className, hasCaret, automationCandidate));
    }

    [Fact]
    public void TextInsertion_BlockingModifierKeys_AreDetectedBeforePaste()
    {
        short KeyState(int key) => key == 0xA5 ? unchecked((short)0x8000) : (short)0;

        Assert.True(WindowsTextInsertionService.HasBlockingModifierKeys(KeyState));
        Assert.False(WindowsTextInsertionService.HasBlockingModifierKeys(_ => 0));
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
