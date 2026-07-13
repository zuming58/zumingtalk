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
    private bool allowClose;
    private DateTimeOffset recordingStartedAt;
    private IAsrSession? asrSession;
    private Task? asrStartupTask;
    private readonly ConcurrentQueue<byte[]> pendingPcmChunks = new();
    private readonly SemaphoreSlim asrSendLock = new(1, 1);
    private string? activeProviderTaskId;
    private string? startupRecognitionError;
    private readonly ITextInsertionService textInsertionService = new WindowsTextInsertionService();
    private CapturedInputTarget? capturedTarget;

    public MainWindow()
    {
        sqliteStore = new SqliteStore(appPaths);
        viewModel = new ShellViewModel(sqliteStore, sqliteStore, sqliteStore, audioPlaybackService, appPaths, new WpfClipboardService(), new AliyunAsrProviderFactory(), new NAudioMicrophoneDeviceService(), new NAudioMicrophoneTestService());
        audioRecorder = new NAudioRecorder(appPaths, () => viewModel.SelectedMicrophone?.DeviceNumber ?? viewModel.Settings.Recognition.MicrophoneDeviceNumber);

        InitializeComponent();

        DataContext = viewModel;
        overlayWindow.DataContext = viewModel;

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        hotkeyService.HotkeyPressed += OnHotkeyPressed;
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
        }
        catch (Exception ex)
        {
            viewModel.Toast = new ToastViewModel($"初始化本地数据失败：{ex.Message}", ToastKind.Error);
        }

        hotkeyService.Start();
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
        if (!isRecording)
        {
            await StartRecordingAsync();
            return;
        }

        await FinishRecordingAsync();
    }

    private async Task StartRecordingAsync()
    {
        try
        {
            recordingStartedAt = DateTimeOffset.Now;
            activeProviderTaskId = null;
            startupRecognitionError = null;
            capturedTarget = textInsertionService.CaptureCurrentTarget();
            pendingPcmChunks.Clear();
            audioRecorder.PcmAudioAvailable += OnPcmAudioAvailable;
            await audioRecorder.StartAsync(CancellationToken.None);
            isRecording = true;
            recordingLimitTimer.Start();
            overlayHoldTimer.Stop();
            viewModel.OverlayState = DictationState.Recording;
            asrStartupTask = StartAsrSessionAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            viewModel.OverlayState = DictationState.Failed;
            viewModel.Toast = new ToastViewModel($"麦克风录音启动失败：{ex.Message}", ToastKind.Error);
            HoldThenHideOverlay(1200);
        }
    }

    private async Task FinishRecordingAsync()
    {
        try
        {
            viewModel.OverlayState = DictationState.Recognizing;
            recordingLimitTimer.Stop();
            var recording = await audioRecorder.StopAsync(CancellationToken.None);
            isRecording = false;
            audioRecorder.PcmAudioAvailable -= OnPcmAudioAvailable;

            var (text, retryCount, errorMessage) = await FinishRecognitionWithRetryAsync(recording.AudioPath, CancellationToken.None);
            var succeeded = string.IsNullOrWhiteSpace(errorMessage);
            var finalText = succeeded ? text : string.Empty;
            var insertionMethod = TextInsertionMethod.CopyOnly;
            var inserted = false;
            var insertionBlocked = false;
            if (succeeded && !string.IsNullOrWhiteSpace(finalText) && capturedTarget is not null)
            {
                var insertion = await TryInsertFinalTextAsync(capturedTarget, finalText);
                insertionMethod = insertion.Method;
                inserted = insertion.Succeeded;
                insertionBlocked = !insertion.Succeeded && insertion.Method == TextInsertionMethod.CopyFallback;
            }

            var record = new TranscriptionRecord(
                Guid.NewGuid(),
                succeeded ? TranscriptionStatus.Completed : TranscriptionStatus.Failed,
                recordingStartedAt,
                recording.Duration,
                finalText,
                recording.AudioPath,
                "Aliyun",
                activeProviderTaskId,
                retryCount,
                finalText.Length,
                insertionMethod,
                ErrorMessage: errorMessage);

            await sqliteStore.UpsertAsync(record, CancellationToken.None);
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
                ? new ToastViewModel(inserted ? "识别结果已写入并保存" : "识别结果已保存到历史记录", ToastKind.Success)
                : new ToastViewModel($"识别失败，录音已保留：{errorMessage}", ToastKind.Error);
            HoldThenHideOverlay(900);
        }
        catch (Exception ex)
        {
            isRecording = false;
            audioRecorder.PcmAudioAvailable -= OnPcmAudioAvailable;
            recordingLimitTimer.Stop();
            await SaveFailedRecordingIfPossibleAsync(ex.Message);
            viewModel.OverlayState = DictationState.Failed;
            viewModel.Toast = new ToastViewModel($"录音保存失败：{ex.Message}", ToastKind.Error);
            HoldThenHideOverlay(1200);
        }
    }

    private async Task CancelRecordingAsync()
    {
        try
        {
            await audioRecorder.CancelAsync(CancellationToken.None);
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
            recordingLimitTimer.Stop();
            audioRecorder.PcmAudioAvailable -= OnPcmAudioAvailable;
            asrStartupTask = null;
            viewModel.OverlayState = DictationState.Idle;
            capturedTarget = null;
        }
    }

    private async Task StartAsrSessionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var asrProvider = await CreateAsrProviderAsync();
            var session = await asrProvider.StartSessionAsync(cancellationToken);
            asrSession = session;
            activeProviderTaskId = session.ProviderTaskId;
            await FlushPendingPcmAsync(cancellationToken);
        }
        catch (Exception asrError)
        {
            startupRecognitionError = asrError.Message;
        }
    }

    private async Task<TextInsertionResult> TryInsertFinalTextAsync(CapturedInputTarget target, string finalText)
    {
        if (target.Kind != InputTargetKind.Editable)
        {
            return new TextInsertionResult(false, TextInsertionMethod.CopyOnly, "No editable target; history only.");
        }

        var result = await textInsertionService.InsertAsync(target, finalText, CancellationToken.None);
        if (!result.Succeeded && result.Method == TextInsertionMethod.CopyFallback)
        {
            viewModel.OverlayState = DictationState.InsertionBlocked;
            viewModel.Toast = new ToastViewModel("未能自动写入，文字已复制", ToastKind.Info);
        }

        return result;
    }

    private void OnPcmAudioAvailable(object? sender, PcmAudioAvailableEventArgs e)
    {
        pendingPcmChunks.Enqueue(e.Buffer);
        _ = FlushPendingPcmAsync(CancellationToken.None);
    }

    private async Task FlushPendingPcmAsync(CancellationToken cancellationToken)
    {
        var session = asrSession;
        if (session is null)
        {
            return;
        }

        await asrSendLock.WaitAsync(cancellationToken);
        try
        {
            while (pendingPcmChunks.TryDequeue(out var chunk))
            {
                await session.PushAudioAsync(chunk, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            startupRecognitionError ??= $"实时识别推流失败：{ex.Message}";
            viewModel.Toast = new ToastViewModel(startupRecognitionError, ToastKind.Error);
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

    private async Task<(string Text, int RetryCount, string? ErrorMessage)> FinishRecognitionWithRetryAsync(string audioPath, CancellationToken cancellationToken)
    {
        try
        {
            if (asrStartupTask is not null)
            {
                await asrStartupTask;
            }

            if (!string.IsNullOrWhiteSpace(startupRecognitionError))
            {
                throw new InvalidOperationException(startupRecognitionError);
            }

            if (asrSession is null)
            {
                throw new InvalidOperationException("ASR session was not started.");
            }

            await FlushPendingPcmAsync(cancellationToken);
            var text = await asrSession.FinishAsync(cancellationToken);
            await asrSession.DisposeAsync();
            asrSession = null;
            return (text, 0, null);
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

    private async Task SaveFailedRecordingIfPossibleAsync(string errorMessage)
    {
        try
        {
            var record = new TranscriptionRecord(
                Guid.NewGuid(),
                TranscriptionStatus.Failed,
                recordingStartedAt,
                TimeSpan.Zero,
                string.Empty,
                null,
                "Aliyun",
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

    private async Task<AliyunAsrProvider> CreateAsrProviderAsync()
    {
        var credentials = await sqliteStore.GetAliyunCredentialsAsync(CancellationToken.None);
        return new AliyunAsrProvider(credentials);
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
    }

    private void OnAliyunSecretPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.PasswordBox passwordBox)
        {
            viewModel.AliyunAccessKeySecret = passwordBox.Password;
        }
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
        }
    }

    private void PositionOverlay()
    {
        var workArea = GetForegroundMonitorWorkArea();
        overlayWindow.Left = workArea.Left + (workArea.Width - overlayWindow.Width) / 2;
        overlayWindow.Top = workArea.Bottom - overlayWindow.Height - 12;
    }

    private static Rect GetForegroundMonitorWorkArea()
    {
        var foreground = GetForegroundWindow();
        var monitor = MonitorFromWindow(foreground, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            return new Rect(
                info.rcWork.Left,
                info.rcWork.Top,
                info.rcWork.Right - info.rcWork.Left,
                info.rcWork.Bottom - info.rcWork.Top);
        }

        return SystemParameters.WorkArea;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

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

public sealed class AliyunAsrProviderFactory : IAsrProviderFactory
{
    public IAsrProvider Create(Zumingtalk.Domain.Settings.AliyunCredentialSettings credentials) => new AliyunAsrProvider(credentials);
}
