namespace Zumingtalk.Domain.Services;

public interface IAudioPlaybackService
{
    Task PlayAsync(string audioPath, CancellationToken cancellationToken);
}
