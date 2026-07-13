namespace Zumingtalk.Domain.Services;

public interface IAudioRecorder
{
    event EventHandler<AudioLevelChangedEventArgs>? LevelChanged;

    event EventHandler<PcmAudioAvailableEventArgs>? PcmAudioAvailable;

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

public sealed class PcmAudioAvailableEventArgs : EventArgs
{
    public PcmAudioAvailableEventArgs(byte[] buffer)
    {
        Buffer = buffer;
    }

    public byte[] Buffer { get; }
}

public sealed record AudioRecordingResult(string AudioPath, TimeSpan Duration);
