namespace Zumingtalk.Domain.Services;

public interface IMicrophoneDeviceService
{
    IReadOnlyList<MicrophoneDevice> ListDevices();
}

public sealed record MicrophoneDevice(int DeviceNumber, string Name);
