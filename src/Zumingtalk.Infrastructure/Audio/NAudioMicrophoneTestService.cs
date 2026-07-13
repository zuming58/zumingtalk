using NAudio.Wave;
using Zumingtalk.Domain.Services;

namespace Zumingtalk.Infrastructure.Audio;

public sealed class NAudioMicrophoneTestService : IMicrophoneTestService
{
    public async Task TestAsync(int deviceNumber, CancellationToken cancellationToken)
    {
        var receivedAudio = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var waveIn = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 100
        };

        waveIn.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded > 0)
            {
                receivedAudio.TrySetResult();
            }
        };
        waveIn.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null)
            {
                receivedAudio.TrySetException(e.Exception);
            }
        };

        waveIn.StartRecording();
        try
        {
            var completed = await Task.WhenAny(receivedAudio.Task, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken));
            if (!ReferenceEquals(completed, receivedAudio.Task))
            {
                throw new TimeoutException("Microphone did not produce audio during the test window.");
            }

            await receivedAudio.Task;
        }
        finally
        {
            waveIn.StopRecording();
        }
    }
}
