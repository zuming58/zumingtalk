using Zumingtalk.Application.Dictation;
using Zumingtalk.Domain.Dictation;
using Zumingtalk.Domain.Services;

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
    public void MockDataFactory_ProvidesSixConfirmedPrototypeRecords()
    {
        var records = Application.DesignTime.MockDataFactory.CreateRecords();

        Assert.Equal(6, records.Count);
        Assert.True(records[0].IsNewest);
        Assert.Contains("百分之多少", records[0].Text);
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

        public Task<TextInsertionResult> InsertAsync(CapturedInputTarget target, string text, CancellationToken cancellationToken)
        {
            InsertWasCalled = true;
            return Task.FromResult(new TextInsertionResult(succeeds, succeeds ? TextInsertionMethod.NativeReplaceSelection : TextInsertionMethod.CopyFallback, "ok"));
        }
    }
}
