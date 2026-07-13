using NAudio.Wave;
using Zumingtalk.Domain.Services;

namespace Zumingtalk.Infrastructure.Audio;

public sealed class NAudioMicrophoneDeviceService : IMicrophoneDeviceService
{
    public IReadOnlyList<MicrophoneDevice> ListDevices()
    {
        var devices = new List<MicrophoneDevice>();
        for (var index = 0; index < WaveInEvent.DeviceCount; index++)
        {
            var capabilities = WaveInEvent.GetCapabilities(index);
            devices.Add(new MicrophoneDevice(index, capabilities.ProductName));
        }

        return devices;
    }
}
