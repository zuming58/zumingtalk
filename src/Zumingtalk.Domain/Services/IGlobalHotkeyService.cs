namespace Zumingtalk.Domain.Services;

public interface IGlobalHotkeyService
{
    event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;

    event EventHandler<HotkeyRegistrationStatus>? RegistrationStatusChanged;

    HotkeyRegistrationStatus RegistrationStatus { get; }

    void Start();

    void SetFallbackHotkeyEnabled(bool enabled);

    void Stop();
}

public sealed record HotkeyRegistrationStatus(
    bool PrimaryHookActive,
    bool FallbackHotkeyEnabled,
    bool FallbackHotkeyRegistered,
    int? PrimaryHookError,
    int? FallbackHotkeyError);

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
