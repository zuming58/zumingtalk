using System.Diagnostics;
using NAudio.Wave;
using Zumingtalk.Domain.Services;

namespace Zumingtalk.Infrastructure.Audio;

public sealed class NAudioRecorder : IAudioRecorder, IDisposable
{
    private readonly IAppPaths appPaths;
    private WaveInEvent? waveIn;
    private WaveFileWriter? writer;
    private Stopwatch? stopwatch;
    private string? currentAudioPath;

    public NAudioRecorder(IAppPaths appPaths)
    {
        this.appPaths = appPaths;
    }

    public event EventHandler<AudioLevelChangedEventArgs>? LevelChanged;

    public event EventHandler<PcmAudioAvailableEventArgs>? PcmAudioAvailable;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (waveIn is not null)
        {
            throw new InvalidOperationException("Recording is already active.");
        }

        Directory.CreateDirectory(appPaths.RecordingsDirectory);
        currentAudioPath = Path.Combine(appPaths.RecordingsDirectory, $"{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}.wav");
        waveIn = new WaveInEvent
        {
            DeviceNumber = 0,
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 100
        };
        writer = new WaveFileWriter(currentAudioPath, waveIn.WaveFormat);
        stopwatch = Stopwatch.StartNew();

        waveIn.DataAvailable += OnDataAvailable;
        waveIn.RecordingStopped += OnRecordingStopped;
        waveIn.StartRecording();
        return Task.CompletedTask;
    }

    public Task<AudioRecordingResult> StopAsync(CancellationToken cancellationToken)
    {
        if (waveIn is null || writer is null || stopwatch is null || currentAudioPath is null)
        {
            throw new InvalidOperationException("Recording is not active.");
        }

        var path = currentAudioPath;
        var duration = stopwatch.Elapsed;
        waveIn.StopRecording();
        DisposeRecordingObjects();
        return Task.FromResult(new AudioRecordingResult(path, duration));
    }

    public Task CancelAsync(CancellationToken cancellationToken)
    {
        var path = currentAudioPath;
        waveIn?.StopRecording();
        DisposeRecordingObjects();

        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        DisposeRecordingObjects();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        writer?.Write(e.Buffer, 0, e.BytesRecorded);
        writer?.Flush();

        var buffer = e.Buffer.AsSpan(0, e.BytesRecorded).ToArray();
        PcmAudioAvailable?.Invoke(this, new PcmAudioAvailableEventArgs(buffer));
        LevelChanged?.Invoke(this, new AudioLevelChangedEventArgs(CalculatePeak(buffer)));
    }

    private static double CalculatePeak(byte[] buffer)
    {
        if (buffer.Length < 2)
        {
            return 0;
        }

        var peak = 0;
        for (var index = 0; index + 1 < buffer.Length; index += 2)
        {
            var sample = Math.Abs(BitConverter.ToInt16(buffer, index));
            if (sample > peak)
            {
                peak = sample;
            }
        }

        return peak / 32768d;
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            Debug.WriteLine(e.Exception);
        }
    }

    private void DisposeRecordingObjects()
    {
        stopwatch?.Stop();
        stopwatch = null;

        if (waveIn is not null)
        {
            waveIn.DataAvailable -= OnDataAvailable;
            waveIn.RecordingStopped -= OnRecordingStopped;
            waveIn.Dispose();
            waveIn = null;
        }

        writer?.Dispose();
        writer = null;
        currentAudioPath = null;
    }
}
