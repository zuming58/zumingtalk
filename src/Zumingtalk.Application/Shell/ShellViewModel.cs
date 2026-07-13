using System.Collections.ObjectModel;
using System.Diagnostics;
using Zumingtalk.Application.Common;
using Zumingtalk.Application.DesignTime;
using Zumingtalk.Domain.Dictation;
using Zumingtalk.Domain.Services;
using Zumingtalk.Domain.Settings;

namespace Zumingtalk.Application.Shell;

public sealed class ShellViewModel : ObservableObject
{
    private readonly IHistoryRepository? historyRepository;
    private readonly IStatisticsRepository? statisticsRepository;
    private readonly ISettingsRepository? settingsRepository;
    private readonly IAudioPlaybackService? audioPlaybackService;
    private readonly IAppPaths? appPaths;
    private readonly IClipboardService? clipboardService;
    private string selectedPage = "Home";
    private TranscriptionRecordViewModel? selectedRecord;
    private TranscriptionRecordViewModel? menuRecord;
    private ToastViewModel? toast;
    private DictationState overlayState = DictationState.Idle;
    private bool detailsOpen;
    private TranscriptionRecordViewModel? lastDeletedRecord;
    private DictationStatistics statistics;
    private AppSettings settings;

    public ShellViewModel()
        : this(null, null, null, null, null)
    {
    }

    public ShellViewModel(
        IHistoryRepository? historyRepository,
        IStatisticsRepository? statisticsRepository,
        ISettingsRepository? settingsRepository,
        IAudioPlaybackService? audioPlaybackService,
        IAppPaths? appPaths,
        IClipboardService? clipboardService = null)
    {
        this.historyRepository = historyRepository;
        this.statisticsRepository = statisticsRepository;
        this.settingsRepository = settingsRepository;
        this.audioPlaybackService = audioPlaybackService;
        this.appPaths = appPaths;
        this.clipboardService = clipboardService;

        Records = MockDataFactory.CreateRecords();
        statistics = MockDataFactory.CreateStatistics();
        settings = MockDataFactory.CreateSettings();

        ShowHomeCommand = new RelayCommand(_ => SelectedPage = "Home");
        ShowSettingsCommand = new RelayCommand(_ => SelectedPage = "Settings");
        CopyCommand = new RelayCommand(CopyRecord, parameter => parameter is TranscriptionRecordViewModel);
        DeleteCommand = new RelayCommand(DeleteRecord, parameter => parameter is TranscriptionRecordViewModel);
        UndoDeleteCommand = new RelayCommand(_ => UndoDelete(), _ => lastDeletedRecord is not null);
        ToggleMenuCommand = new RelayCommand(ToggleMenu, parameter => parameter is TranscriptionRecordViewModel);
        OpenDetailsCommand = new RelayCommand(OpenDetails, parameter => parameter is TranscriptionRecordViewModel);
        CloseDetailsCommand = new RelayCommand(_ => DetailsOpen = false);
        PlayCommand = new RelayCommand(PlayRecord, parameter => parameter is TranscriptionRecordViewModel);
        RetranscribeCommand = new RelayCommand(_ => ShowToast("已提交重新转写", ToastKind.Success));
        SaveSettingsCommand = new RelayCommand(SaveSettings);
        TestConnectionCommand = new RelayCommand(_ => ShowToast("连接测试需要阿里云凭证，将在 M3 接入实时识别", ToastKind.Info));
        TestInsertionCommand = new RelayCommand(_ => ShowToast("自动写入测试将在 M4 使用真实目标捕获", ToastKind.Info));
        OpenRecordingsFolderCommand = new RelayCommand(OpenRecordingsFolder);
        ToggleOverlayCommand = new RelayCommand(_ => ToggleDictationDemo());
        ShowBlockedOverlayCommand = new RelayCommand(_ => OverlayState = DictationState.InsertionBlocked);
        ClearToastCommand = new RelayCommand(_ => Toast = null);
    }

    public ObservableCollection<TranscriptionRecordViewModel> Records { get; }

    public DictationStatistics Statistics
    {
        get => statistics;
        private set
        {
            if (SetProperty(ref statistics, value))
            {
                OnPropertyChanged(nameof(TotalDurationText));
                OnPropertyChanged(nameof(TotalCharactersText));
                OnPropertyChanged(nameof(AverageSpeedText));
            }
        }
    }

    public AppSettings Settings
    {
        get => settings;
        private set => SetProperty(ref settings, value);
    }

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

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (settingsRepository is not null)
        {
            Settings = await settingsRepository.GetAsync(cancellationToken);
        }

        if (historyRepository is not null)
        {
            await historyRepository.InitializeAsync(cancellationToken);
            var records = await historyRepository.ListRecentAsync(Settings.LocalData.RetentionDays, cancellationToken);
            Records.Clear();
            foreach (var item in records.Select((record, index) => new TranscriptionRecordViewModel(record, index == 0)))
            {
                Records.Add(item);
            }
        }

        if (statisticsRepository is not null)
        {
            Statistics = await statisticsRepository.GetAsync(cancellationToken);
        }
    }

    public async Task ReloadRecordsAsync(CancellationToken cancellationToken)
    {
        if (historyRepository is null)
        {
            return;
        }

        var records = await historyRepository.ListRecentAsync(Settings.LocalData.RetentionDays, cancellationToken);
        Records.Clear();
        foreach (var item in records.Select((record, index) => new TranscriptionRecordViewModel(record, index == 0)))
        {
            Records.Add(item);
        }

        if (statisticsRepository is not null)
        {
            Statistics = await statisticsRepository.GetAsync(cancellationToken);
        }
    }

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
        _ = PersistDeleteAsync(record.Record.Id);
        ShowToast("已删除这条记录", ToastKind.Info, showUndo: true);
    }

    private void UndoDelete()
    {
        if (lastDeletedRecord is null)
        {
            return;
        }

        var restored = lastDeletedRecord;
        Records.Add(restored);
        var ordered = Records.OrderByDescending(item => item.Record.StartedAt).ToList();
        Records.Clear();
        foreach (var item in ordered)
        {
            Records.Add(item);
        }

        lastDeletedRecord = null;
        UndoDeleteCommand.RaiseCanExecuteChanged();
        _ = PersistRestoreAsync(restored.Record);
        ShowToast("记录已恢复", ToastKind.Success);
    }

    private void CopyRecord(object? parameter)
    {
        if (parameter is not TranscriptionRecordViewModel record)
        {
            return;
        }

        try
        {
            if (clipboardService is null)
            {
                ShowToast("剪贴板服务尚未初始化", ToastKind.Error);
                return;
            }

            clipboardService.SetText(record.Text);
            ShowToast("已复制到剪贴板", ToastKind.Success);
        }
        catch (Exception ex)
        {
            ShowToast($"复制失败：{ex.Message}", ToastKind.Error);
        }
    }

    private void PlayRecord(object? parameter)
    {
        if (parameter is not TranscriptionRecordViewModel record || audioPlaybackService is null)
        {
            ShowToast("这条记录没有可播放的录音", ToastKind.Error);
            return;
        }

        var audioPath = record.Record.AudioPath;
        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
        {
            ShowToast("录音文件不存在", ToastKind.Error);
            return;
        }

        _ = PlayRecordAsync(audioPath);
    }

    private async Task PlayRecordAsync(string audioPath)
    {
        try
        {
            ShowToast("正在播放这段录音", ToastKind.Info);
            await audioPlaybackService!.PlayAsync(audioPath, CancellationToken.None);
        }
        catch (Exception ex)
        {
            ShowToast($"播放失败：{ex.Message}", ToastKind.Error);
        }
    }

    private void OpenRecordingsFolder(object? parameter)
    {
        try
        {
            var directory = appPaths?.RecordingsDirectory ?? Settings.LocalData.RecordingsDirectory;
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{directory}\"",
                UseShellExecute = true
            });
            ShowToast("已打开录音文件夹", ToastKind.Success);
        }
        catch (Exception ex)
        {
            ShowToast($"打开失败：{ex.Message}", ToastKind.Error);
        }
    }

    private void SaveSettings(object? parameter)
    {
        if (settingsRepository is null)
        {
            ShowToast("设置已保存", ToastKind.Success);
            return;
        }

        _ = SaveSettingsAsync();
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            await settingsRepository!.SaveAsync(Settings, CancellationToken.None);
            ShowToast("设置已保存", ToastKind.Success);
        }
        catch (Exception ex)
        {
            ShowToast($"保存失败：{ex.Message}", ToastKind.Error);
        }
    }

    private async Task PersistDeleteAsync(Guid id)
    {
        if (historyRepository is null)
        {
            return;
        }

        try
        {
            await historyRepository.DeleteAsync(id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            ShowToast($"删除失败：{ex.Message}", ToastKind.Error);
        }
    }

    private async Task PersistRestoreAsync(TranscriptionRecord record)
    {
        if (historyRepository is null)
        {
            return;
        }

        try
        {
            await historyRepository.UpsertAsync(record, CancellationToken.None);
        }
        catch (Exception ex)
        {
            ShowToast($"恢复失败：{ex.Message}", ToastKind.Error);
        }
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
