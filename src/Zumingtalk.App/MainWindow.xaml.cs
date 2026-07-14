using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Zumingtalk.App.Windows;
using Zumingtalk.Application.History;
using Zumingtalk.Application.Shell;
using Zumingtalk.Domain.Dictation;
using Zumingtalk.Domain.Services;
using Zumingtalk.Infrastructure.Audio;
using Zumingtalk.Infrastructure.Asr;
using Zumingtalk.Infrastructure.Storage;
using Zumingtalk.Infrastructure.Windows;
using Forms = System.Windows.Forms;

namespace Zumingtalk.App;

public partial class MainWindow : Window
{
    private readonly IAppPaths appPaths = new AppPaths();
    private readonly SqliteStore sqliteStore;
    private readonly IAudioRecorder audioRecorder;
    private readonly IAudioPlaybackService audioPlaybackService = new NAudioPlaybackService();
    private readonly OverlayWindow overlayWindow = new();
    private readonly IGlobalHotkeyService hotkeyService = new GlobalHotkeyService();
    private readonly DispatcherTimer overlayHoldTimer = new();
    private readonly DispatcherTimer recordingLimitTimer = new();
    private readonly Forms.NotifyIcon trayIcon = new();
    private ShellViewModel viewModel;
    private bool isRecording;
    private bool isProcessingDictation;
    private bool allowClose;
    private DateTimeOffset recordingStartedAt;
    private IAsrSession? asrSession;
    private Task? asrStartupTask;
    private readonly ConcurrentQueue<byte[]> pendingPcmChunks = new();
    private readonly SemaphoreSlim asrSendLock = new(1, 1);
    private CancellationTokenSource? activeDictationCts;
    private int activeDictationId;
    private string? activeProviderTaskId;
    private string? startupRecognitionError;
    private readonly ITextInsertionService textInsertionService = new WindowsTextInsertionService();
    private CapturedInputTarget? capturedTarget;

    public MainWindow()
    {
        sqliteStore = new SqliteStore(appPaths);
        viewModel = new ShellViewModel(sqliteStore, sqliteStore, sqliteStore, audioPlaybackService, appPaths, new WpfClipboardService(), new BailianAsrProviderFactory(), new NAudioMicrophoneDeviceService(), new NAudioMicrophoneTestService(), textInsertionService);
        audioRecorder = new NAudioRecorder(appPaths, () => viewModel.SelectedMicrophone?.DeviceNumber ?? viewModel.Settings.Recognition.MicrophoneDeviceNumber);

        InitializeComponent();

        DataContext = viewModel;
        overlayWindow.DataContext = viewModel;

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        hotkeyService.HotkeyPressed += OnHotkeyPressed;
        hotkeyService.RegistrationStatusChanged += OnHotkeyRegistrationStatusChanged;
        audioRecorder.LevelChanged += OnAudioLevelChanged;
        overlayHoldTimer.Tick += OnOverlayHoldTimerTick;
        overlayHoldTimer.Interval = TimeSpan.FromMilliseconds(900);
        recordingLimitTimer.Tick += OnRecordingLimitTimerTick;
        recordingLimitTimer.Interval = TimeSpan.FromMinutes(10);

        ConfigureTrayIcon();

        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.System && e.SystemKey == Key.RightAlt)
        {
            _ = ToggleRecordingAsync();
            e.Handled = true;
        }

        if (e.Key == Key.Escape && isRecording)
        {
            _ = CancelRecordingAsync();
            e.Handled = true;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await sqliteStore.InitializeAsync(CancellationToken.None);
            await new RetentionService(sqliteStore, appPaths).CleanupAsync(3, CancellationToken.None);
            await viewModel.InitializeAsync(CancellationToken.None);
            hotkeyService.SetFallbackHotkeyEnabled(viewModel.Settings.Hotkeys.FallbackHotkeyEnabled);
        }
        catch (Exception ex)
        {
            viewModel.Toast = new ToastViewModel($"初始化本地数据失败：{ex.Message}", ToastKind.Error);
        }

        hotkeyService.Start();
        viewModel.UpdateHotkeyRegistrationStatus(hotkeyService.RegistrationStatus);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
        trayIcon.Visible = true;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        hotkeyService.Stop();
        hotkeyService.RegistrationStatusChanged -= OnHotkeyRegistrationStatusChanged;
        audioRecorder.LevelChanged -= OnAudioLevelChanged;
        audioRecorder.PcmAudioAvailable -= OnPcmAudioAvailable;
        if (audioRecorder is IDisposable disposableRecorder)
        {
            disposableRecorder.Dispose();
        }

        overlayWindow.Close();
        asrSendLock.Dispose();
        trayIcon.Visible = false;
        trayIcon.Dispose();
    }

    private void ConfigureTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => ShowFromTray());
        menu.Items.Add("打开录音文件夹", null, (_, _) => viewModel.OpenRecordingsFolderCommand.Execute(null));
        menu.Items.Add("退出", null, (_, _) => ExitApplication());

        trayIcon.Icon = System.Drawing.SystemIcons.Application;
        trayIcon.Text = "祖名闪电说";
        trayIcon.Visible = true;
        trayIcon.ContextMenuStrip = menu;
        trayIcon.DoubleClick += (_, _) => ShowFromTray();
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        allowClose = true;
        trayIcon.Visible = false;
        Close();
    }

    private void OnHotkeyPressed(object? sender, HotkeyPressedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (e.Action == HotkeyAction.ToggleDictation)
            {
                _ = ToggleRecordingAsync();
            }
            else if (e.Action == HotkeyAction.CancelDictation && isRecording)
            {
                _ = CancelRecordingAsync();
            }
        });
    }

    private async Task ToggleRecordingAsync()
    {
        if (isProcessingDictation && !isRecording)
        {
            return;
        }

        if (!isRecording)
        {
            await StartRecordingAsync();
            return;
        }

        await FinishRecordingAsync();
    }

    private async Task StartRecordingAsync()
    {
        if (isProcessingDictation)
        {
            return;
        }

        isProcessingDictation = true;
        var dictationCts = new CancellationTokenSource();
        var dictationId = Interlocked.Increment(ref activeDictationId);
        try
        {
            activeDictationCts = dictationCts;
            recordingStartedAt = DateTimeOffset.Now;
            activeProviderTaskId = null;
            startupRecognitionError = null;
            asrSession = null;
            asrStartupTask = null;
            capturedTarget = textInsertionService.CaptureCurrentTarget();
            viewModel.UpdateCapturedTarget(capturedTarget);
            pendingPcmChunks.Clear();
            audioRecorder.PcmAudioAvailable += OnPcmAudioAvailable;
            await audioRecorder.StartAsync(dictationCts.Token);
            isRecording = true;
            recordingLimitTimer.Start();
            overlayHoldTimer.Stop();
            viewModel.OverlayState = DictationState.Recording;
            asrStartupTask = StartAsrSessionAsync(dictationId, dictationCts.Token);
        }
        catch (Exception ex)
        {
            audioRecorder.PcmAudioAvailable -= OnPcmAudioAvailable;
            dictationCts.Cancel();
            dictationCts.Dispose();
            if (ReferenceEquals(activeDictationCts, dictationCts))
            {
                activeDictationCts = null;
            }

            viewModel.OverlayState = DictationState.Failed;
            viewModel.Toast = new ToastViewModel($"麦克风录音启动失败：{ex.Message}", ToastKind.Error);
            HoldThenHideOverlay(1200);
            isProcessingDictation = false;
        }
    }

    private async Task FinishRecordingAsync()
    {
        var dictationCts = activeDictationCts;
        var dictationId = activeDictationId;
        var cancellationToken = dictationCts?.Token ?? CancellationToken.None;
        var recordId = Guid.NewGuid();
        AudioRecordingResult? recording = null;
        var recordPersisted = false;
        try
        {
            viewModel.OverlayState = DictationState.Recognizing;
            recordingLimitTimer.Stop();
            recording = await audioRecorder.StopAsync(cancellationToken);
            isRecording = false;
            audioRecorder.PcmAudioAvailable -= OnPcmAudioAvailable;

            var (text, retryCount, errorMessage) = await FinishRecognitionWithRetryAsync(dictationId, recording.AudioPath, cancellationToken);
            var succeeded = string.IsNullOrWhiteSpace(errorMessage);
            var finalText = succeeded ? text : string.Empty;
            var insertionMethod = TextInsertionMethod.CopyOnly;
            var inserted = false;
            var insertionBlocked = false;
            if (succeeded && !string.IsNullOrWhiteSpace(finalText) &&
                viewModel.Settings.Compatibility.PreferredMode == TextInsertionMethod.CopyOnly)
            {
                var insertion = await textInsertionService.CopyOnlyAsync(finalText, cancellationToken);
                viewModel.UpdateInsertionResult(insertion);
                insertionMethod = insertion.Method;
                insertionBlocked = true;
            }
            else if (succeeded && !string.IsNullOrWhiteSpace(finalText) && capturedTarget is not null)
            {
                var insertion = await TryInsertFinalTextAsync(capturedTarget, finalText, cancellationToken);
                viewModel.UpdateInsertionResult(insertion);
                insertionMethod = insertion.Method;
                inserted = insertion.Succeeded;
                insertionBlocked = !insertion.Succeeded &&
                    insertion.Method is TextInsertionMethod.CopyFallback or TextInsertionMethod.SendInputPaste;
            }

            var record = new TranscriptionRecord(
                recordId,
                succeeded ? TranscriptionStatus.Completed : TranscriptionStatus.Failed,
                recordingStartedAt,
                recording.Duration,
                finalText,
                recording.AudioPath,
                "阿里云百炼 Fun-ASR",
                activeProviderTaskId,
                retryCount,
                finalText.Length,
                insertionMethod,
                ErrorMessage: errorMessage);

            await sqliteStore.UpsertAsync(record, CancellationToken.None);
            recordPersisted = true;
            if (succeeded)
            {
                await sqliteStore.AddCompletedAsync(record.Duration, record.CharacterCount, CancellationToken.None);
            }
            else
            {
                await sqliteStore.AddFailedDurationAsync(record.Duration, CancellationToken.None);
            }
            await viewModel.ReloadRecordsAsync(CancellationToken.None);

            viewModel.OverlayState = succeeded
                ? (inserted ? DictationState.Completed : insertionBlocked ? DictationState.InsertionBlocked : DictationState.Saved)
                : DictationState.Failed;
            viewModel.Toast = succeeded
                ? insertionMethod switch
                {
                    _ when inserted => new ToastViewModel("识别结果已写入并保存", ToastKind.Success),
                    TextInsertionMethod.SendInputPaste => new ToastViewModel("未能确认自动写入，文字已复制，可直接粘贴", ToastKind.Info),
                    TextInsertionMethod.CopyFallback => new ToastViewModel("自动写入受阻，文字已复制", ToastKind.Info),
                    TextInsertionMethod.CopyOnly => new ToastViewModel("识别结果已保存，文字已复制", ToastKind.Success),
                    _ => new ToastViewModel("识别结果已保存到历史记录", ToastKind.Success)
                }
                : new ToastViewModel($"识别失败，录音已保留：{errorMessage}", ToastKind.Error);
            HoldThenHideOverlay(900);
        }
        catch (Exception ex)
        {
            isRecording = false;
            audioRecorder.PcmAudioAvailable -= OnPcmAudioAvailable;
            recordingLimitTimer.Stop();
            if (asrSession is not null)
            {
                await asrSession.CancelAsync(CancellationToken.None);
                await asrSession.DisposeAsync();
                asrSession = null;
            }

            if (!recordPersisted)
            {
                await SaveFailedRecordingIfPossibleAsync(recordId, recording, ex.Message);
            }
            viewModel.OverlayState = DictationState.Failed;
            viewModel.Toast = new ToastViewModel($"听写处理失败，录音已保留：{ex.Message}", ToastKind.Error);
            HoldThenHideOverlay(1200);
        }
        finally
        {
            DisposeActiveDictationCts(dictationCts);
            isProcessingDictation = false;
        }
    }

    private async Task CancelRecordingAsync()
    {
        var dictationCts = activeDictationCts;
        dictationCts?.Cancel();
        try
        {
            await audioRecorder.CancelAsync(CancellationToken.None);
            if (asrStartupTask is not null)
            {
                await asrStartupTask;
            }

            if (asrSession is not null)
            {
                await asrSession.CancelAsync(CancellationToken.None);
                await asrSession.DisposeAsync();
                asrSession = null;
            }
        }
        catch (Exception ex)
        {
            viewModel.Toast = new ToastViewModel($"取消录音失败：{ex.Message}", ToastKind.Error);
        }
        finally
        {
            isRecording = false;
            isProcessingDictation = false;
            recordingLimitTimer.Stop();
            audioRecorder.PcmAudioAvailable -= OnPcmAudioAvailable;
            pendingPcmChunks.Clear();
            viewModel.OverlayState = DictationState.Idle;
            capturedTarget = null;
            DisposeActiveDictationCts(dictationCts);
        }
    }

    private async Task StartAsrSessionAsync(int dictationId, CancellationToken cancellationToken)
    {
        try
        {
            var asrProvider = await CreateAsrProviderAsync();
            var session = await asrProvider.StartSessionAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested || dictationId != activeDictationId)
            {
                await session.CancelAsync(CancellationToken.None);
                await session.DisposeAsync();
                return;
            }

            asrSession = session;
            activeProviderTaskId = session.ProviderTaskId;
            await FlushPendingPcmAsync(dictationId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception asrError)
        {
            if (!cancellationToken.IsCancellationRequested && dictationId == activeDictationId)
            {
                startupRecognitionError = asrError.Message;
            }
        }
    }

    private async Task<TextInsertionResult> TryInsertFinalTextAsync(CapturedInputTarget target, string finalText, CancellationToken cancellationToken)
    {
        target = textInsertionService.ValidateCapturedTarget(target);
        viewModel.UpdateCapturedTarget(target);
        if (target.Kind == InputTargetKind.Lost)
        {
            return new TextInsertionResult(false, TextInsertionMethod.Auto, "Captured target was lost; history only.");
        }

        if (target.Kind != InputTargetKind.Editable)
        {
            return new TextInsertionResult(false, TextInsertionMethod.Auto, "No editable target; history only.");
        }

        var result = await textInsertionService.InsertAsync(target, finalText, cancellationToken);
        if (!result.Succeeded && result.Method is TextInsertionMethod.CopyFallback or TextInsertionMethod.SendInputPaste)
        {
            viewModel.OverlayState = DictationState.InsertionBlocked;
            viewModel.Toast = new ToastViewModel(result.Method == TextInsertionMethod.SendInputPaste
                ? "未能确认自动写入，文字已复制，可直接粘贴"
                : "自动写入受阻，文字已复制", ToastKind.Info);
        }

        return result;
    }

    private void OnPcmAudioAvailable(object? sender, PcmAudioAvailableEventArgs e)
    {
        pendingPcmChunks.Enqueue(e.Buffer);
        var dictationCts = activeDictationCts;
        if (dictationCts is not null)
        {
            _ = FlushPendingPcmAsync(activeDictationId, dictationCts.Token);
        }
    }

    private async Task FlushPendingPcmAsync(int dictationId, CancellationToken cancellationToken)
    {
        var session = asrSession;
        if (session is null || dictationId != activeDictationId)
        {
            return;
        }

        await asrSendLock.WaitAsync(cancellationToken);
        try
        {
            if (dictationId != activeDictationId || !ReferenceEquals(session, asrSession))
            {
                return;
            }

            while (pendingPcmChunks.TryDequeue(out var chunk))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (dictationId != activeDictationId)
                {
                    return;
                }

                await session.PushAudioAsync(chunk, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (dictationId == activeDictationId)
            {
                startupRecognitionError ??= $"实时识别推流失败：{ex.Message}";
                viewModel.Toast = new ToastViewModel(startupRecognitionError, ToastKind.Error);
            }
        }
        finally
        {
            asrSendLock.Release();
        }
    }

    private async void OnRecordingLimitTimerTick(object? sender, EventArgs e)
    {
        recordingLimitTimer.Stop();
        if (isRecording)
        {
            await FinishRecordingAsync();
        }
    }

    private async Task<(string Text, int RetryCount, string? ErrorMessage)> FinishRecognitionWithRetryAsync(int dictationId, string audioPath, CancellationToken cancellationToken)
    {
        try
        {
            if (asrStartupTask is not null)
            {
                await asrStartupTask;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (dictationId != activeDictationId)
            {
                throw new OperationCanceledException("Dictation session was superseded.", cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(startupRecognitionError))
            {
                throw new InvalidOperationException(startupRecognitionError);
            }

            if (asrSession is null)
            {
                throw new InvalidOperationException("ASR session was not started.");
            }

            await FlushPendingPcmAsync(dictationId, cancellationToken);
            var text = await asrSession.FinishAsync(cancellationToken);
            await asrSession.DisposeAsync();
            asrSession = null;
            return (text, 0, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (asrSession is not null)
            {
                await asrSession.CancelAsync(CancellationToken.None);
                await asrSession.DisposeAsync();
                asrSession = null;
            }

            throw;
        }
        catch (Exception firstError)
        {
            if (asrSession is not null)
            {
                await asrSession.DisposeAsync();
                asrSession = null;
            }

            try
            {
                var retryProvider = await CreateAsrProviderAsync();
                var text = await retryProvider.RetranscribeAsync(audioPath, cancellationToken);
                return (text, 1, null);
            }
            catch (Exception retryError)
            {
                return (string.Empty, 1, $"{firstError.Message}; retry failed: {retryError.Message}");
            }
        }
    }

    private async Task SaveFailedRecordingIfPossibleAsync(Guid recordId, AudioRecordingResult? recording, string errorMessage)
    {
        try
        {
            var record = new TranscriptionRecord(
                recordId,
                TranscriptionStatus.Failed,
                recordingStartedAt,
                recording?.Duration ?? TimeSpan.Zero,
                string.Empty,
                recording?.AudioPath,
                "阿里云百炼 Fun-ASR",
                activeProviderTaskId,
                0,
                0,
                TextInsertionMethod.CopyOnly,
                ErrorMessage: errorMessage);
            await sqliteStore.UpsertAsync(record, CancellationToken.None);
            await viewModel.ReloadRecordsAsync(CancellationToken.None);
        }
        catch
        {
            // Last-resort failure recording must not crash the app.
        }
    }

    private async Task<BailianFunAsrProvider> CreateAsrProviderAsync()
    {
        var credentials = await sqliteStore.GetBailianCredentialsAsync(CancellationToken.None);
        return new BailianFunAsrProvider(credentials, viewModel.Settings.Recognition.SemanticPunctuationEnabled);
    }

    private void OnAudioLevelChanged(object? sender, AudioLevelChangedEventArgs e)
    {
        Dispatcher.Invoke(() => overlayWindow.SetAudioLevel(e.Level));
    }

    private void OnOverlayHoldTimerTick(object? sender, EventArgs e)
    {
        overlayHoldTimer.Stop();
        if (!isRecording)
        {
            viewModel.OverlayState = DictationState.Idle;
        }
    }

    private void HoldThenHideOverlay(int milliseconds)
    {
        overlayHoldTimer.Stop();
        overlayHoldTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        overlayHoldTimer.Start();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.OverlayState))
        {
            SyncOverlayWindow();
        }
        else if (e.PropertyName == nameof(ShellViewModel.Settings) || e.PropertyName == nameof(ShellViewModel.FallbackHotkeyEnabled))
        {
            hotkeyService.SetFallbackHotkeyEnabled(viewModel.Settings.Hotkeys.FallbackHotkeyEnabled);
        }
    }

    private void OnHotkeyRegistrationStatusChanged(object? sender, HotkeyRegistrationStatus e)
    {
        Dispatcher.Invoke(() => viewModel.UpdateHotkeyRegistrationStatus(e));
    }

    private void OnBailianApiKeyPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.PasswordBox passwordBox)
        {
            viewModel.BailianApiKey = passwordBox.Password;
        }
    }

    private void OnScrollHomeToTop(object sender, RoutedEventArgs e)
    {
        HomeRecordsScrollViewer.ScrollToTop();
    }

    private void OnHomeRecordsScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        viewModel.IsBackToTopVisible = e.VerticalOffset > 120;
    }

    private void DisposeActiveDictationCts(CancellationTokenSource? dictationCts)
    {
        if (dictationCts is null)
        {
            return;
        }

        if (ReferenceEquals(activeDictationCts, dictationCts))
        {
            activeDictationCts = null;
            asrStartupTask = null;
            capturedTarget = null;
            pendingPcmChunks.Clear();
        }

        dictationCts.Dispose();
    }

    private void SyncOverlayWindow()
    {
        var state = viewModel.OverlayState;

        if (state == DictationState.Idle)
        {
            overlayWindow.Hide();
            return;
        }

        PositionOverlay();
        overlayWindow.ApplyState(state);

        if (!overlayWindow.IsVisible)
        {
            overlayWindow.Show();
            PositionOverlay();
        }
    }

    private void PositionOverlay()
    {
        var foreground = GetForegroundWindow();
        var dpi = GetDpiForTargetWindow(foreground);

        var workArea = GetForegroundMonitorWorkArea(foreground, dpi);
        overlayWindow.PositionOverWorkArea(workArea, dpi);
    }

    private static uint GetDpiForTargetWindow(IntPtr foreground)
    {
        try
        {
            var dpi = foreground == IntPtr.Zero ? 0 : GetDpiForWindow(foreground);
            return dpi == 0 ? 96u : dpi;
        }
        catch (EntryPointNotFoundException)
        {
            return 96;
        }
    }

    private static PhysicalWorkArea GetForegroundMonitorWorkArea(IntPtr foreground, uint dpi)
    {
        var monitor = MonitorFromWindow(foreground, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            return new PhysicalWorkArea(
                info.rcWork.Left,
                info.rcWork.Top,
                info.rcWork.Right,
                info.rcWork.Bottom);
        }

        var scale = dpi / 96d;
        var fallback = SystemParameters.WorkArea;
        return new PhysicalWorkArea(
            (int)Math.Round(fallback.Left * scale),
            (int)Math.Round(fallback.Top * scale),
            (int)Math.Round(fallback.Right * scale),
            (int)Math.Round(fallback.Bottom * scale));
    }

    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

public sealed class BailianAsrProviderFactory : IAsrProviderFactory
{
    public IAsrProvider Create(Zumingtalk.Domain.Settings.BailianCredentialSettings credentials, bool semanticPunctuationEnabled) =>
        new BailianFunAsrProvider(credentials, semanticPunctuationEnabled);
}
