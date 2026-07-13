using System;
using System.ComponentModel;
using System.IO;
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
    private readonly Forms.NotifyIcon trayIcon = new();
    private ShellViewModel viewModel;
    private bool isRecording;
    private bool allowClose;
    private DateTimeOffset recordingStartedAt;

    public MainWindow()
    {
        sqliteStore = new SqliteStore(appPaths);
        audioRecorder = new NAudioRecorder(appPaths);
        viewModel = new ShellViewModel(sqliteStore, sqliteStore, sqliteStore, audioPlaybackService, appPaths, new WpfClipboardService());

        InitializeComponent();

        DataContext = viewModel;
        overlayWindow.DataContext = viewModel;

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        hotkeyService.HotkeyPressed += OnHotkeyPressed;
        audioRecorder.LevelChanged += OnAudioLevelChanged;
        overlayHoldTimer.Tick += OnOverlayHoldTimerTick;
        overlayHoldTimer.Interval = TimeSpan.FromMilliseconds(900);

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
        if (audioRecorder is IDisposable disposableRecorder)
        {
            disposableRecorder.Dispose();
        }

        overlayWindow.Close();
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
            await audioRecorder.StartAsync(CancellationToken.None);
            isRecording = true;
            overlayHoldTimer.Stop();
            viewModel.OverlayState = DictationState.Recording;
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
            var recording = await audioRecorder.StopAsync(CancellationToken.None);
            isRecording = false;

            var record = new TranscriptionRecord(
                Guid.NewGuid(),
                TranscriptionStatus.Completed,
                recordingStartedAt,
                recording.Duration,
                "本次录音已保存，等待 M3 接入阿里云实时识别后生成转写文本。",
                recording.AudioPath,
                "Local WAV",
                null,
                0,
                0,
                TextInsertionMethod.CopyOnly);

            await sqliteStore.UpsertAsync(record, CancellationToken.None);
            await sqliteStore.AddFailedDurationAsync(record.Duration, CancellationToken.None);
            await viewModel.ReloadRecordsAsync(CancellationToken.None);

            viewModel.OverlayState = DictationState.Saved;
            viewModel.Toast = new ToastViewModel("录音已保存到历史记录", ToastKind.Success);
            HoldThenHideOverlay(900);
        }
        catch (Exception ex)
        {
            isRecording = false;
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
        }
        catch (Exception ex)
        {
            viewModel.Toast = new ToastViewModel($"取消录音失败：{ex.Message}", ToastKind.Error);
        }
        finally
        {
            isRecording = false;
            viewModel.OverlayState = DictationState.Idle;
        }
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
        var workArea = SystemParameters.WorkArea;
        overlayWindow.Left = workArea.Left + (workArea.Width - overlayWindow.Width) / 2;
        overlayWindow.Top = workArea.Bottom - overlayWindow.Height - 12;
    }
}
