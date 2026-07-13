using NAudio.Wave;
using Zumingtalk.Domain.Services;

namespace Zumingtalk.Infrastructure.Audio;

public sealed class NAudioPlaybackService : IAudioPlaybackService
{
    public Task PlayAsync(string audioPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
        {
            throw new FileNotFoundException("Audio file was not found.", audioPath);
        }

        using var audioFile = new AudioFileReader(audioPath);
        using var outputDevice = new WaveOutEvent();
        outputDevice.Init(audioFile);
        outputDevice.Play();

        while (outputDevice.PlaybackState == PlaybackState.Playing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.Sleep(50);
        }

        return Task.CompletedTask;
    }
}
