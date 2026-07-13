using Zumingtalk.Application.Common;
using Zumingtalk.Domain.Dictation;

namespace Zumingtalk.Application.DesignTime;

public sealed class TranscriptionRecordViewModel : ObservableObject
{
    private bool isMenuOpen;

    public TranscriptionRecordViewModel(TranscriptionRecord record, bool isNewest)
    {
        Record = record;
        IsNewest = isNewest;
    }

    public TranscriptionRecord Record { get; }

    public bool IsNewest { get; }

    public bool IsMenuOpen
    {
        get => isMenuOpen;
        set => SetProperty(ref isMenuOpen, value);
    }

    public string Id => Record.Id.ToString("N");

    public string Text => Record.FinalText;

    public string DurationText => Record.Duration.ToString(@"mm\:ss");

    public string TimeText => Record.StartedAt.ToString("HH:mm");

    public string DateTimeText => Record.StartedAt.ToString("yyyy-MM-dd HH:mm");

    public string Provider => Record.Provider;

    public string StatusText => Record.Status == TranscriptionStatus.Completed ? "已完成" : Record.Status.ToString();

    public string RetryCountText => Record.RetryCount.ToString();

    public string Source => Record.Source;

    public string InsertionMethodText => Record.InsertionMethod switch
    {
        TextInsertionMethod.NativeReplaceSelection => "原生编辑插入",
        TextInsertionMethod.PasteMessage => "标准粘贴消息",
        TextInsertionMethod.SendInputPaste => "输入模拟",
        TextInsertionMethod.CopyFallback => "复制兜底",
        TextInsertionMethod.CopyOnly => "仅复制",
        _ => "自动选择"
    };
}
