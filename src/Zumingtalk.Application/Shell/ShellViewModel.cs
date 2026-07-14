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
    private readonly IAsrProviderFactory? asrProviderFactory;
    private readonly IMicrophoneDeviceService? microphoneDeviceService;
    private readonly IMicrophoneTestService? microphoneTestService;
    private readonly ITextInsertionService? textInsertionService;
    private string selectedPage = "Home";
    private TranscriptionRecordViewModel? selectedRecord;
    private TranscriptionRecordViewModel? menuRecord;
    private ToastViewModel? toast;
    private DictationState overlayState = DictationState.Idle;
    private bool detailsOpen;
    private TranscriptionRecordViewModel? lastDeletedRecord;
    private CancellationTokenSource? undoWindowCancellation;
    private DictationStatistics statistics;
    private AppSettings settings;
    private string bailianApiKey = string.Empty;
    private string primaryHotkeyStatusText = "未启动";
    private string fallbackHotkeyStatusText = "未启动";
    private string lastCapturedTargetText = "尚未捕获";
    private string lastInsertionMethodText = "尚未写入";
    private string lastInsertionStatusText = "尚未写入";
    private bool isBackToTopVisible;
    private MicrophoneDevice? selectedMicrophone;

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
        IClipboardService? clipboardService = null,
        IAsrProviderFactory? asrProviderFactory = null,
        IMicrophoneDeviceService? microphoneDeviceService = null,
        IMicrophoneTestService? microphoneTestService = null,
        ITextInsertionService? textInsertionService = null)
    {
        this.historyRepository = historyRepository;
        this.statisticsRepository = statisticsRepository;
        this.settingsRepository = settingsRepository;
        this.audioPlaybackService = audioPlaybackService;
        this.appPaths = appPaths;
        this.clipboardService = clipboardService;
        this.asrProviderFactory = asrProviderFactory;
        this.microphoneDeviceService = microphoneDeviceService;
        this.microphoneTestService = microphoneTestService;
        this.textInsertionService = textInsertionService;

        Records = MockDataFactory.CreateRecords();
        Microphones = new ObservableCollection<MicrophoneDevice>();
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
        RetranscribeCommand = new RelayCommand(RetranscribeRecord, parameter => ResolveRecord(parameter) is not null);
        SaveSettingsCommand = new RelayCommand(SaveSettings);
        TestConnectionCommand = new RelayCommand(_ => _ = TestConnectionAsync());
        TestInsertionCommand = new RelayCommand(_ => _ = TestInsertionAsync());
        OpenRecordingsFolderCommand = new RelayCommand(OpenRecordingsFolder);
        ToggleOverlayCommand = new RelayCommand(_ => ToggleDictationDemo());
        ShowBlockedOverlayCommand = new RelayCommand(_ => OverlayState = DictationState.InsertionBlocked);
        ClearToastCommand = new RelayCommand(_ => Toast = null);
    }

    public ObservableCollection<TranscriptionRecordViewModel> Records { get; }

    public ObservableCollection<MicrophoneDevice> Microphones { get; }

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

    public string BailianApiKey
    {
        get => bailianApiKey;
        set => SetProperty(ref bailianApiKey, value);
    }

    public MicrophoneDevice? SelectedMicrophone
    {
        get => selectedMicrophone;
        set
        {
            if (SetProperty(ref selectedMicrophone, value) && value is not null)
            {
                Settings = Settings with
                {
                    Recognition = Settings.Recognition with
                    {
                        MicrophoneName = value.Name,
                        MicrophoneDeviceNumber = value.DeviceNumber
                    }
                };
            }
        }
    }

    public bool SemanticPunctuationEnabled
    {
        get => Settings.Recognition.SemanticPunctuationEnabled;
        set
        {
            if (value == Settings.Recognition.SemanticPunctuationEnabled)
            {
                return;
            }

            Settings = Settings with { Recognition = Settings.Recognition with { SemanticPunctuationEnabled = value } };
            OnPropertyChanged();
        }
    }

    public bool FallbackHotkeyEnabled
    {
        get => Settings.Hotkeys.FallbackHotkeyEnabled;
        set
        {
            if (value == Settings.Hotkeys.FallbackHotkeyEnabled)
            {
                return;
            }

            Settings = Settings with { Hotkeys = Settings.Hotkeys with { FallbackHotkeyEnabled = value } };
            OnPropertyChanged();
        }
    }

    public TextInsertionMethod PreferredInsertionMode
    {
        get => Settings.Compatibility.PreferredMode;
        set
        {
            if (value == Settings.Compatibility.PreferredMode)
            {
                return;
            }

            Settings = Settings with { Compatibility = Settings.Compatibility with { PreferredMode = value } };
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<TextInsertionModeOption> InsertionModeOptions { get; } =
    [
        new(TextInsertionMethod.Auto, "自动选择"),
        new(TextInsertionMethod.CopyOnly, "仅复制")
    ];

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

    public string TodayText => DateTimeOffset.Now.ToString("yyyy-MM-dd");

    public string PrimaryHotkeyStatusText
    {
        get => primaryHotkeyStatusText;
        private set => SetProperty(ref primaryHotkeyStatusText, value);
    }

    public string FallbackHotkeyStatusText
    {
        get => fallbackHotkeyStatusText;
        private set => SetProperty(ref fallbackHotkeyStatusText, value);
    }

    public bool IsBackToTopVisible
    {
        get => isBackToTopVisible;
        set => SetProperty(ref isBackToTopVisible, value);
    }

    public string LastCapturedTargetText
    {
        get => lastCapturedTargetText;
        private set => SetProperty(ref lastCapturedTargetText, value);
    }

    public string LastInsertionMethodText
    {
        get => lastInsertionMethodText;
        private set => SetProperty(ref lastInsertionMethodText, value);
    }

    public string LastInsertionStatusText
    {
        get => lastInsertionStatusText;
        private set => SetProperty(ref lastInsertionStatusText, value);
    }

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
            NotifySettingsDerivedProperties();
            var credentials = await settingsRepository.GetBailianCredentialsAsync(cancellationToken);
            LoadBailianCredentialField(credentials);
        }

        LoadMicrophones();

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

    private void LoadMicrophones()
    {
        Microphones.Clear();
        var devices = microphoneDeviceService?.ListDevices() ?? [];
        foreach (var device in devices)
        {
            Microphones.Add(device);
        }

        SelectedMicrophone = Microphones.FirstOrDefault(device => device.DeviceNumber == Settings.Recognition.MicrophoneDeviceNumber)
            ?? Microphones.FirstOrDefault();
    }

    private void NotifySettingsDerivedProperties()
    {
        OnPropertyChanged(nameof(SemanticPunctuationEnabled));
        OnPropertyChanged(nameof(FallbackHotkeyEnabled));
        OnPropertyChanged(nameof(PreferredInsertionMode));
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

    public void UpdateCapturedTarget(CapturedInputTarget target)
    {
        LastCapturedTargetText = target.Kind switch
        {
            InputTargetKind.Editable => string.IsNullOrWhiteSpace(target.ProcessName) ? "可编辑目标" : target.ProcessName,
            InputTargetKind.None => "无输入目标",
            InputTargetKind.Lost => "目标已丢失",
            _ => "尚未捕获"
        };
    }

    public void UpdateInsertionResult(TextInsertionResult result)
    {
        LastInsertionMethodText = result.Method switch
        {
            TextInsertionMethod.NativeReplaceSelection => "原生插入",
            TextInsertionMethod.PasteMessage => "窗口粘贴",
            TextInsertionMethod.SendInputPaste => "快捷键粘贴",
            TextInsertionMethod.CopyFallback => "复制兜底",
            TextInsertionMethod.CopyOnly => "仅复制",
            _ => "自动选择"
        };

        LastInsertionStatusText = result.Succeeded
            ? "已确认写入"
            : result.Method == TextInsertionMethod.SendInputPaste
                ? "已尝试写入"
                : result.Method is TextInsertionMethod.CopyFallback or TextInsertionMethod.CopyOnly
                    ? "文字已复制"
                    : "仅保存历史";
    }

    internal async Task RetranscribeRecordAsync(TranscriptionRecordViewModel recordViewModel, CancellationToken cancellationToken)
    {
        if (historyRepository is null || settingsRepository is null || asrProviderFactory is null)
        {
            ShowToast("重新转写服务尚未初始化", ToastKind.Error);
            return;
        }

        var record = recordViewModel.Record;
        if (string.IsNullOrWhiteSpace(record.AudioPath) || !File.Exists(record.AudioPath))
        {
            ShowToast("录音文件不存在，无法重新转写", ToastKind.Error);
            return;
        }

        try
        {
            ShowToast("正在重新转写录音", ToastKind.Info);
            var credentials = await settingsRepository.GetBailianCredentialsAsync(cancellationToken);
            var provider = asrProviderFactory.Create(credentials, Settings.Recognition.SemanticPunctuationEnabled);
            var text = await provider.RetranscribeAsync(record.AudioPath, cancellationToken);
            var updated = record with
            {
                Status = TranscriptionStatus.Completed,
                FinalText = text,
                CharacterCount = text.Length,
                RetryCount = record.RetryCount + 1,
                Provider = "阿里云百炼 Fun-ASR",
                ErrorMessage = null
            };

            await historyRepository.UpsertAsync(updated, cancellationToken);
            if (statisticsRepository is not null)
            {
                var successfulDurationDelta = record.Status == TranscriptionStatus.Completed ? TimeSpan.Zero : record.Duration;
                var characterCountDelta = record.Status == TranscriptionStatus.Completed ? updated.CharacterCount - record.CharacterCount : updated.CharacterCount;
                await statisticsRepository.AdjustCompletedAsync(TimeSpan.Zero, successfulDurationDelta, characterCountDelta, cancellationToken);
            }

            await ReloadRecordsAsync(cancellationToken);
            SelectedRecord = Records.FirstOrDefault(item => item.Record.Id == updated.Id);
            ShowToast("重新转写已完成", ToastKind.Success);
        }
        catch (Exception ex)
        {
            var failed = record with
            {
                Status = TranscriptionStatus.Failed,
                RetryCount = record.RetryCount + 1,
                ErrorMessage = ex.Message
            };
            await historyRepository.UpsertAsync(failed, CancellationToken.None);
            await ReloadRecordsAsync(CancellationToken.None);
            SelectedRecord = Records.FirstOrDefault(item => item.Record.Id == failed.Id);
            ShowToast($"重新转写失败，录音已保留：{ex.Message}", ToastKind.Error);
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
        StartUndoWindow();
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
        undoWindowCancellation?.Cancel();
        UndoDeleteCommand.RaiseCanExecuteChanged();
        _ = PersistRestoreAsync(restored.Record);
        ShowToast("记录已恢复", ToastKind.Success);
    }

    private void StartUndoWindow()
    {
        undoWindowCancellation?.Cancel();
        undoWindowCancellation = new CancellationTokenSource();
        _ = ExpireUndoAsync(undoWindowCancellation.Token);
    }

    private async Task ExpireUndoAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            lastDeletedRecord = null;
            UndoDeleteCommand.RaiseCanExecuteChanged();
            if (Toast?.ShowUndo == true)
            {
                Toast = null;
            }
        }
        catch (OperationCanceledException)
        {
        }
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

    private void RetranscribeRecord(object? parameter)
    {
        var record = ResolveRecord(parameter);
        if (record is null)
        {
            ShowToast("请选择一条录音记录", ToastKind.Error);
            return;
        }

        _ = RetranscribeRecordAsync(record, CancellationToken.None);
    }

    private TranscriptionRecordViewModel? ResolveRecord(object? parameter)
    {
        return parameter as TranscriptionRecordViewModel ?? SelectedRecord;
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

    internal async Task SaveSettingsAsync()
    {
        try
        {
            await settingsRepository!.SaveAsync(Settings, CancellationToken.None);
            await SaveBailianCredentialFieldAsync(CancellationToken.None);
            ShowToast("设置已保存", ToastKind.Success);
        }
        catch (Exception ex)
        {
            ShowToast($"保存失败：{ex.Message}", ToastKind.Error);
        }
    }

    internal async Task TestConnectionAsync()
    {
        if (settingsRepository is null || asrProviderFactory is null)
        {
            ShowToast("连接测试服务尚未初始化", ToastKind.Error);
            return;
        }

        try
        {
            await SaveBailianCredentialFieldAsync(CancellationToken.None);
            var credentials = await settingsRepository.GetBailianCredentialsAsync(CancellationToken.None);
            var provider = asrProviderFactory.Create(credentials, Settings.Recognition.SemanticPunctuationEnabled);
            await provider.TestConnectionAsync(CancellationToken.None);
            if (microphoneTestService is not null)
            {
                await microphoneTestService.TestAsync(Settings.Recognition.MicrophoneDeviceNumber, CancellationToken.None);
            }

            ShowToast("百炼 Fun-ASR 连接与麦克风测试通过", ToastKind.Success);
        }
        catch (Exception ex)
        {
            ShowToast($"百炼连接测试失败：{ex.Message}", ToastKind.Error);
        }
    }

    internal async Task TestInsertionAsync()
    {
        if (textInsertionService is null)
        {
            ShowToast("自动写入服务尚未初始化", ToastKind.Error);
            return;
        }

        try
        {
            const string testText = "祖名闪电说写入测试";
            if (PreferredInsertionMode == TextInsertionMethod.CopyOnly)
            {
                var copyResult = await textInsertionService.CopyOnlyAsync(testText, CancellationToken.None);
                UpdateInsertionResult(copyResult);
                ShowToast("仅复制模式已生效，测试文字已复制", ToastKind.Success);
                return;
            }

            ShowToast("3 秒后测试，请切换到目标输入框", ToastKind.Info);
            await Task.Delay(TimeSpan.FromSeconds(3));
            var target = textInsertionService.CaptureCurrentTarget();
            UpdateCapturedTarget(target);
            target = textInsertionService.ValidateCapturedTarget(target);
            UpdateCapturedTarget(target);
            var result = await textInsertionService.InsertAsync(target, testText, CancellationToken.None);
            UpdateInsertionResult(result);
            var message = result.Method switch
            {
                _ when result.Succeeded => "自动写入测试成功",
                TextInsertionMethod.SendInputPaste => "已尝试自动写入，测试文字保留在剪贴板",
                TextInsertionMethod.CopyFallback => "自动写入受阻，测试文字已复制",
                TextInsertionMethod.Auto => "未捕获到原输入框，请重新测试",
                _ => "未确认自动写入，请查看目标输入框"
            };
            ShowToast(message, result.Succeeded ? ToastKind.Success : ToastKind.Info);
        }
        catch (Exception ex)
        {
            ShowToast($"自动写入测试失败：{ex.Message}", ToastKind.Error);
        }
    }

    private async Task SaveBailianCredentialFieldAsync(CancellationToken cancellationToken)
    {
        if (settingsRepository is null)
        {
            return;
        }

        var existing = await settingsRepository.GetBailianCredentialsAsync(cancellationToken);
        var credentials = existing with
        {
            ApiKey = string.IsNullOrWhiteSpace(BailianApiKey)
                ? existing.ApiKey
                : BailianApiKey.Trim()
        };

        await settingsRepository.SaveBailianCredentialsAsync(credentials, cancellationToken);
        Settings = await settingsRepository.GetAsync(cancellationToken);
        NotifySettingsDerivedProperties();
        LoadBailianCredentialField(credentials, clearSecret: true);
    }

    private void LoadBailianCredentialField(BailianCredentialSettings credentials, bool clearSecret = true)
    {
        if (clearSecret)
        {
            BailianApiKey = string.Empty;
        }
        else
        {
            BailianApiKey = credentials.ApiKey;
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

    public void UpdateHotkeyRegistrationStatus(HotkeyRegistrationStatus status)
    {
        PrimaryHotkeyStatusText = status.PrimaryHookActive
            ? "右 Alt 已注册"
            : $"右 Alt 未注册{FormatWin32Error(status.PrimaryHookError)}";

        FallbackHotkeyStatusText = !status.FallbackHotkeyEnabled
            ? "备用热键已关闭"
            : status.FallbackHotkeyRegistered
                ? $"{Settings.Hotkeys.FallbackHotkey} 已注册"
                : $"{Settings.Hotkeys.FallbackHotkey} 未注册{FormatWin32Error(status.FallbackHotkeyError)}";
    }

    private static string FormatWin32Error(int? errorCode) =>
        errorCode is null or 0 ? string.Empty : $"（Win32 {errorCode}）";
}

public sealed record ToastViewModel(string Message, ToastKind Kind, bool ShowUndo = false);

public enum ToastKind
{
    Info,
    Success,
    Error
}

public sealed record TextInsertionModeOption(TextInsertionMethod Value, string Label);
