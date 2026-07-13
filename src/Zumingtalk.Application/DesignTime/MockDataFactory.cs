using System.Collections.ObjectModel;
using Zumingtalk.Domain.Dictation;
using Zumingtalk.Domain.Settings;

namespace Zumingtalk.Application.DesignTime;

public static class MockDataFactory
{
    private static readonly DateTimeOffset Today = new(2026, 7, 13, 22, 18, 0, TimeSpan.FromHours(8));

    public static ObservableCollection<TranscriptionRecordViewModel> CreateRecords()
    {
        var records = new[]
        {
            ("00:44", "22:18", "比方说我们功能方面开发的现在有百分之多少了？"),
            ("00:43", "21:46", "我们已经开发了四个阶段了，就是我也没有，我也不知道到底每个阶段到底是做的什么东西。就是现在，这个那个页面不是搭好了嘛，对吧？然后的话之前是一个，那看的效果还不错，然后现在主要是做了哪些方面？就是具体内容方面我们已经完成了哪些？一共分了多少个阶段。"),
            ("00:36", "20:31", "坦白讲，这个投资管家，大家看起来是有点一脸懵逼的感觉。好像这个里面是核心池、卫星池，都是股票交易的吗？不是，一开始说的是大部分是什么核心，核心池不都是什么这个债，指数基金啊，黄金货币这些东西吗？我以前没做过相关东西，有点一脸懵逼的感觉。"),
            ("00:26", "19:58", "那尚未完成的测试继续完成吧。对，把尚未完成的把它完成掉。"),
            ("00:26", "18:42", "你的意思是第三、第四阶段另外一个 Codex 的开发的，你全部审计并且修正完了，是吧？之前不是说发现了很多问题，要补充很多问题吗？都已经 OK 了吗？"),
            ("00:23", "17:20", "现在刚才那个用量没有了，然后你看现在是开发到哪一步了？做到哪一步了？")
        };

        var result = new ObservableCollection<TranscriptionRecordViewModel>();

        for (var index = 0; index < records.Length; index++)
        {
            var item = records[index];
            var duration = TimeSpan.ParseExact(item.Item1, @"mm\:ss", null);
            var timeParts = item.Item2.Split(':');
            var started = Today.Date.AddHours(int.Parse(timeParts[0])).AddMinutes(int.Parse(timeParts[1]));
            var record = new TranscriptionRecord(
                Guid.NewGuid(),
                TranscriptionStatus.Completed,
                started,
                duration,
                item.Item3,
                AudioPath: $"recordings\\2026-07-13-{index + 1:00}.wav",
                Provider: "阿里云实时识别",
                ProviderTaskId: $"mock-task-{index + 1:000}",
                RetryCount: 0,
                CharacterCount: item.Item3.Length,
                InsertionMethod: index == 0 ? TextInsertionMethod.SendInputPaste : TextInsertionMethod.Auto);

            result.Add(new TranscriptionRecordViewModel(record, index == 0));
        }

        return result;
    }

    public static DictationStatistics CreateStatistics() =>
        new(TimeSpan.FromHours(12) + TimeSpan.FromMinutes(33), 157451, 209);

    public static AppSettings CreateSettings() =>
        new(
            new RecognitionSettings(
                Provider: "阿里云智能语音交互",
                AppKeyPreview: "a9f2••••••••7c1",
                AccessKeyIdPreview: "LTAI5t••••••••••••",
                OralSmoothingEnabled: true,
                MicrophoneName: "系统默认麦克风"),
            new HotkeySettings(
                PrimaryHotkey: "右 Alt",
                FallbackHotkeyEnabled: true,
                FallbackHotkey: "Ctrl + Win + Space"),
            new CompatibilitySettings(
                LastTargetApplication: "Codex.exe",
                LastInsertionMethod: TextInsertionMethod.SendInputPaste,
                LastInsertionSucceeded: true,
                PreferredMode: TextInsertionMethod.Auto),
            new LocalDataSettings(
                RecordingsDirectory: "%LOCALAPPDATA%\\Zumingtalk\\recordings",
                RetentionDays: 3));
}
