namespace Zumingtalk.Infrastructure.Audio;

public sealed class AudioLevelMeter
{
    private const double EnterSpeechDb = -42;
    private const double ExitSpeechDb = -48;
    private const double SilenceHoldMilliseconds = 220;
    private double visualLevel;
    private double quietMilliseconds;
    private bool speaking;

    public double ProcessPcm16Mono(byte[] buffer, int sampleRate = 16000)
    {
        var db = CalculateDbfs(buffer);
        var durationMs = buffer.Length <= 0 || sampleRate <= 0
            ? 0
            : (buffer.Length / 2d / sampleRate) * 1000d;

        if (db >= EnterSpeechDb)
        {
            speaking = true;
            quietMilliseconds = 0;
        }
        else if (db <= ExitSpeechDb)
        {
            quietMilliseconds += durationMs;
            if (quietMilliseconds >= SilenceHoldMilliseconds)
            {
                speaking = false;
                visualLevel = 0;
                return 0;
            }
        }
        else
        {
            quietMilliseconds = 0;
        }

        if (!speaking)
        {
            return 0;
        }

        var rawVisual = Math.Clamp((db + 50) / 35, 0d, 1d);
        var target = Math.Pow(rawVisual, 0.62);
        var coefficient = target > visualLevel ? 0.64 : 0.20;
        visualLevel += (target - visualLevel) * coefficient;
        return Math.Clamp(visualLevel, 0d, 1d);
    }

    public static double CalculateDbfs(byte[] buffer)
    {
        if (buffer.Length < 2)
        {
            return -120;
        }

        double sumSquares = 0;
        var sampleCount = 0;
        for (var index = 0; index + 1 < buffer.Length; index += 2)
        {
            var sample = BitConverter.ToInt16(buffer, index) / 32768d;
            sumSquares += sample * sample;
            sampleCount++;
        }

        if (sampleCount == 0)
        {
            return -120;
        }

        var rms = Math.Sqrt(sumSquares / sampleCount);
        return 20 * Math.Log10(Math.Max(rms, 0.000001));
    }
}
