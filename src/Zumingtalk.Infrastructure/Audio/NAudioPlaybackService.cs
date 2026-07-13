using NAudio.Wave;
using Zumingtalk.Domain.Services;

namespace Zumingtalk.Infrastructure.Audio;

public sealed class NAudioPlaybackService : IAudioPlaybackService
{
    public async Task PlayAsync(string audioPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
        {
            throw new FileNotFoundException("Audio file was not found.", audioPath);
        }

        using var audioFile = new AudioFileReader(audioPath);
        using var outputDevice = new WaveOutEvent();
        var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        outputDevice.PlaybackStopped += (_, e) => stopped.TrySetResult(e.Exception);
        outputDevice.Init(audioFile);
        outputDevice.Play();

        using var registration = cancellationToken.Register(() =>
        {
            outputDevice.Stop();
            stopped.TrySetCanceled(cancellationToken);
        });

        var error = await stopped.Task;
        if (error is not null)
        {
            throw error;
        }
    }
}
