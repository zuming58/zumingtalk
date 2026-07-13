using System.Collections.ObjectModel;
using Zumingtalk.Application.Common;
using Zumingtalk.Application.DesignTime;
using Zumingtalk.Domain.Dictation;
using Zumingtalk.Domain.Settings;

namespace Zumingtalk.Application.Shell;

public sealed class ShellViewModel : ObservableObject
{
    private string selectedPage = "Home";
    private TranscriptionRecordViewModel? selectedRecord;
    private TranscriptionRecordViewModel? menuRecord;
    private ToastViewModel? toast;
    private DictationState overlayState = DictationState.Idle;
    private bool detailsOpen;
    private TranscriptionRecordViewModel? lastDeletedRecord;

    public ShellViewModel()
    {
        Records = MockDataFactory.CreateRecords();
        Statistics = MockDataFactory.CreateStatistics();
        Settings = MockDataFactory.CreateSettings();

        ShowHomeCommand = new RelayCommand(_ => SelectedPage = "Home");
        ShowSettingsCommand = new RelayCommand(_ => SelectedPage = "Settings");
        CopyCommand = new RelayCommand(parameter => ShowToast("已复制到剪贴板", ToastKind.Success));
        DeleteCommand = new RelayCommand(DeleteRecord, parameter => parameter is TranscriptionRecordViewModel);
        UndoDeleteCommand = new RelayCommand(_ => UndoDelete(), _ => lastDeletedRecord is not null);
        ToggleMenuCommand = new RelayCommand(ToggleMenu, parameter => parameter is TranscriptionRecordViewModel);
        OpenDetailsCommand = new RelayCommand(OpenDetails, parameter => parameter is TranscriptionRecordViewModel);
        CloseDetailsCommand = new RelayCommand(_ => DetailsOpen = false);
        PlayCommand = new RelayCommand(_ => ShowToast("正在播放这段录音", ToastKind.Info));
        RetranscribeCommand = new RelayCommand(_ => ShowToast("已提交重新转写", ToastKind.Success));
        SaveSettingsCommand = new RelayCommand(_ => ShowToast("设置已保存", ToastKind.Success));
        TestConnectionCommand = new RelayCommand(_ => ShowToast("连接成功，麦克风工作正常", ToastKind.Success));
        TestInsertionCommand = new RelayCommand(_ => ShowToast("测试成功：当前输入框可自动写入", ToastKind.Success));
        OpenRecordingsFolderCommand = new RelayCommand(_ => ShowToast("已打开录音文件夹", ToastKind.Success));
        ToggleOverlayCommand = new RelayCommand(_ => ToggleDictationDemo());
        ShowBlockedOverlayCommand = new RelayCommand(_ => OverlayState = DictationState.InsertionBlocked);
        ClearToastCommand = new RelayCommand(_ => Toast = null);
    }

    public ObservableCollection<TranscriptionRecordViewModel> Records { get; }

    public DictationStatistics Statistics { get; }

    public AppSettings Settings { get; }

    public string SelectedPage
    {
        get => selectedPage;
        set
        {
            if (SetProperty(ref selectedPage, value))
            {
                OnPropertyChanged(nameof(IsHomeSelected));
                OnPropertyChanged(nameof(IsSettingsSelected));
            }
        }
    }

    public bool IsHomeSelected => SelectedPage == "Home";

    public bool IsSettingsSelected => SelectedPage == "Settings";

    public TranscriptionRecordViewModel? SelectedRecord
    {
        get => selectedRecord;
        set => SetProperty(ref selectedRecord, value);
    }

    public TranscriptionRecordViewModel? MenuRecord
    {
        get => menuRecord;
        set => SetProperty(ref menuRecord, value);
    }

    public bool DetailsOpen
    {
        get => detailsOpen;
        set => SetProperty(ref detailsOpen, value);
    }

    public ToastViewModel? Toast
    {
        get => toast;
        set => SetProperty(ref toast, value);
    }

    public DictationState OverlayState
    {
        get => overlayState;
        set
        {
            if (SetProperty(ref overlayState, value))
            {
                OnPropertyChanged(nameof(IsOverlayVisible));
            }
        }
    }

    public bool IsOverlayVisible => OverlayState != DictationState.Idle;

    public string TotalDurationText => $"{(int)Statistics.TotalDuration.TotalHours}时{Statistics.TotalDuration.Minutes:00}分";

    public string TotalCharactersText => $"{Statistics.TotalCharacters:N0}字";

    public string AverageSpeedText => $"{Statistics.AverageCharactersPerMinute}字/分";

    public RelayCommand ShowHomeCommand { get; }

    public RelayCommand ShowSettingsCommand { get; }

    public RelayCommand CopyCommand { get; }

    public RelayCommand DeleteCommand { get; }

    public RelayCommand UndoDeleteCommand { get; }

    public RelayCommand ToggleMenuCommand { get; }

    public RelayCommand OpenDetailsCommand { get; }

    public RelayCommand CloseDetailsCommand { get; }

    public RelayCommand PlayCommand { get; }

    public RelayCommand RetranscribeCommand { get; }

    public RelayCommand SaveSettingsCommand { get; }

    public RelayCommand TestConnectionCommand { get; }

    public RelayCommand TestInsertionCommand { get; }

    public RelayCommand OpenRecordingsFolderCommand { get; }

    public RelayCommand ToggleOverlayCommand { get; }

    public RelayCommand ShowBlockedOverlayCommand { get; }

    public RelayCommand ClearToastCommand { get; }

    private void ToggleMenu(object? parameter)
    {
        if (parameter is not TranscriptionRecordViewModel record)
        {
            return;
        }

        if (MenuRecord is not null && !ReferenceEquals(MenuRecord, record))
        {
            MenuRecord.IsMenuOpen = false;
        }

        if (ReferenceEquals(MenuRecord, record))
        {
            record.IsMenuOpen = false;
            MenuRecord = null;
            return;
        }

        record.IsMenuOpen = true;
        MenuRecord = record;
    }

    private void OpenDetails(object? parameter)
    {
        if (parameter is not TranscriptionRecordViewModel record)
        {
            return;
        }

        SelectedRecord = record;
        if (MenuRecord is not null)
        {
            MenuRecord.IsMenuOpen = false;
            MenuRecord = null;
        }
        DetailsOpen = true;
    }

    private void DeleteRecord(object? parameter)
    {
        if (parameter is not TranscriptionRecordViewModel record)
        {
            return;
        }

        Records.Remove(record);
        if (ReferenceEquals(MenuRecord, record))
        {
            record.IsMenuOpen = false;
            MenuRecord = null;
        }
        lastDeletedRecord = record;
        UndoDeleteCommand.RaiseCanExecuteChanged();
        ShowToast("已删除这条记录", ToastKind.Info, showUndo: true);
    }

    private void UndoDelete()
    {
        if (lastDeletedRecord is null)
        {
            return;
        }

        Records.Add(lastDeletedRecord);
        var ordered = Records.OrderByDescending(item => item.Record.StartedAt).ToList();
        Records.Clear();
        foreach (var item in ordered)
        {
            Records.Add(item);
        }

        lastDeletedRecord = null;
        UndoDeleteCommand.RaiseCanExecuteChanged();
        ShowToast("记录已恢复", ToastKind.Success);
    }

    private void ToggleDictationDemo()
    {
        OverlayState = OverlayState switch
        {
            DictationState.Idle => DictationState.Recording,
            DictationState.Recording => DictationState.Recognizing,
            DictationState.Recognizing => DictationState.Completed,
            _ => DictationState.Idle
        };
    }

    private void ShowToast(string message, ToastKind kind, bool showUndo = false)
    {
        Toast = new ToastViewModel(message, kind, showUndo);
    }
}

public sealed record ToastViewModel(string Message, ToastKind Kind, bool ShowUndo = false);

public enum ToastKind
{
    Info,
    Success,
    Error
}
