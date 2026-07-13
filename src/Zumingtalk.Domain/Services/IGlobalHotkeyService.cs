namespace Zumingtalk.Domain.Services;

public interface IGlobalHotkeyService
{
    event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;

    void Start();

    void Stop();
}

public sealed class HotkeyPressedEventArgs : EventArgs
{
    public HotkeyPressedEventArgs(HotkeyAction action)
    {
        Action = action;
    }

    public HotkeyAction Action { get; }
}

public enum HotkeyAction
{
    ToggleDictation,
    CancelDictation
}
