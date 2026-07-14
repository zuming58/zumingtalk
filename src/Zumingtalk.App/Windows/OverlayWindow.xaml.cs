using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Zumingtalk.Domain.Dictation;

namespace Zumingtalk.App.Windows;

public partial class OverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private double smoothedLevel;
    private DictationState currentState = DictationState.Idle;

    public OverlayWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(handle, GWL_EXSTYLE);
        SetWindowLong(handle, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);
    }

    public void ApplyState(DictationState state)
    {
        if (state == DictationState.Recording && currentState != DictationState.Recording)
        {
            smoothedLevel = 0;
        }

        currentState = state;
        SyncRecordingVisualization();
        DotsPanel.Visibility = state == DictationState.Recognizing ? Visibility.Visible : Visibility.Collapsed;
        ResultGlyph.Visibility = state is DictationState.Completed or DictationState.Saved or DictationState.InsertionBlocked or DictationState.Failed
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public void SetAudioLevel(double level)
    {
        smoothedLevel = Math.Clamp(level, 0d, 1d);
        SyncRecordingVisualization();

        if (smoothedLevel <= 0)
        {
            return;
        }

        var weights = new[] { 0.55, 0.85, 1.0, 0.75, 0.50 };
        WaveBar1.Height = CalculateBarHeight(smoothedLevel, weights[0]);
        WaveBar2.Height = CalculateBarHeight(smoothedLevel, weights[1]);
        WaveBar3.Height = CalculateBarHeight(smoothedLevel, weights[2]);
        WaveBar4.Height = CalculateBarHeight(smoothedLevel, weights[3]);
        WaveBar5.Height = CalculateBarHeight(smoothedLevel, weights[4]);
    }

    private void SyncRecordingVisualization()
    {
        var showSpeech = currentState == DictationState.Recording && smoothedLevel > 0.01;
        var showSilence = currentState == DictationState.Recording && !showSpeech;
        WavePanel.Visibility = showSpeech ? Visibility.Visible : Visibility.Collapsed;
        SilenceLine.Visibility = showSilence ? Visibility.Visible : Visibility.Collapsed;
    }

    private static double CalculateBarHeight(double level, double weight) =>
        4 + (24 * Math.Clamp(level * weight, 0d, 1d));

    public void PositionOverWorkArea(PhysicalWorkArea workArea, uint dpi, double bottomMarginDip = 12)
    {
        var scale = Math.Max(1d, dpi / 96d);
        var width = (int)Math.Round(Width * scale);
        var height = (int)Math.Round(Height * scale);
        var bottomMargin = (int)Math.Round(bottomMarginDip * scale);
        var x = workArea.Left + ((workArea.Width - width) / 2);
        var y = workArea.Bottom - height - bottomMargin;
        var handle = new WindowInteropHelper(this).EnsureHandle();

        _ = SetWindowPos(
            handle,
            HWND_TOPMOST,
            x,
            y,
            width,
            height,
            SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_SHOWWINDOW);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);
}

public readonly record struct PhysicalWorkArea(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;
}
