namespace Zumingtalk.Domain.Services;

public interface IAudioRecorder
{
    event EventHandler<AudioLevelChangedEventArgs>? LevelChanged;

    Task StartAsync(CancellationToken cancellationToken);

    Task<AudioRecordingResult> StopAsync(CancellationToken cancellationToken);

    Task CancelAsync(CancellationToken cancellationToken);
}

public sealed class AudioLevelChangedEventArgs : EventArgs
{
    public AudioLevelChangedEventArgs(double level)
    {
        Level = Math.Clamp(level, 0d, 1d);
    }

    public double Level { get; }
}

public sealed record AudioRecordingResult(string AudioPath, TimeSpan Duration);
