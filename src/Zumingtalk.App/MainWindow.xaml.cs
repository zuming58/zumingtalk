using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Zumingtalk.App.Windows;
using Zumingtalk.Application.Shell;
using Zumingtalk.Domain.Dictation;
using Zumingtalk.Domain.Services;
using Zumingtalk.Infrastructure.Windows;

namespace Zumingtalk.App;

public partial class MainWindow : Window
{
    private readonly ShellViewModel viewModel = new();
    private readonly OverlayWindow overlayWindow = new();
    private readonly IGlobalHotkeyService hotkeyService = new GlobalHotkeyService();
    private readonly DispatcherTimer overlayHoldTimer = new();

    public MainWindow()
    {
        InitializeComponent();

        DataContext = viewModel;
        overlayWindow.DataContext = viewModel;

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        hotkeyService.HotkeyPressed += OnHotkeyPressed;
        overlayHoldTimer.Tick += OnOverlayHoldTimerTick;
        overlayHoldTimer.Interval = TimeSpan.FromMilliseconds(900);

        Loaded += (_, _) => hotkeyService.Start();
        Closed += (_, _) =>
        {
            hotkeyService.Stop();
            overlayWindow.Close();
        };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.System && e.SystemKey == Key.RightAlt)
        {
            ToggleDictationFromHotkey();
            e.Handled = true;
        }

        if (e.Key == Key.Escape && viewModel.OverlayState == DictationState.Recording)
        {
            viewModel.OverlayState = DictationState.Idle;
            e.Handled = true;
        }
    }

    private void OnHotkeyPressed(object? sender, HotkeyPressedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (e.Action == HotkeyAction.ToggleDictation)
            {
                ToggleDictationFromHotkey();
            }
            else if (e.Action == HotkeyAction.CancelDictation && viewModel.OverlayState == DictationState.Recording)
            {
                viewModel.OverlayState = DictationState.Idle;
            }
        });
    }

    private void ToggleDictationFromHotkey()
    {
        overlayHoldTimer.Stop();

        viewModel.OverlayState = viewModel.OverlayState switch
        {
            DictationState.Idle => DictationState.Recording,
            DictationState.Recording => DictationState.Recognizing,
            DictationState.Recognizing => DictationState.Completed,
            _ => DictationState.Idle
        };

        if (viewModel.OverlayState == DictationState.Recognizing)
        {
            overlayHoldTimer.Interval = TimeSpan.FromMilliseconds(900);
            overlayHoldTimer.Start();
        }
    }

    private void OnOverlayHoldTimerTick(object? sender, EventArgs e)
    {
        overlayHoldTimer.Stop();

        if (viewModel.OverlayState == DictationState.Recognizing)
        {
            viewModel.OverlayState = DictationState.Completed;
            overlayHoldTimer.Interval = TimeSpan.FromMilliseconds(650);
            overlayHoldTimer.Start();
            return;
        }

        if (viewModel.OverlayState is DictationState.Completed or DictationState.Saved)
        {
            viewModel.OverlayState = DictationState.Idle;
        }
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
