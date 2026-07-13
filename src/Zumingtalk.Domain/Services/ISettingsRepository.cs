using Zumingtalk.Domain.Settings;

namespace Zumingtalk.Domain.Services;

public interface ISettingsRepository
{
    Task<AppSettings> GetAsync(CancellationToken cancellationToken);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken);

    Task<AliyunCredentialSettings> GetAliyunCredentialsAsync(CancellationToken cancellationToken);

    Task SaveAliyunCredentialsAsync(AliyunCredentialSettings credentials, CancellationToken cancellationToken);
}
