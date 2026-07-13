namespace Zumingtalk.Domain.Services;

public interface IMicrophoneTestService
{
    Task TestAsync(int deviceNumber, CancellationToken cancellationToken);
}
