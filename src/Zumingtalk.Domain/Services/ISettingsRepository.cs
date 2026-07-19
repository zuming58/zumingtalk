using Zumingtalk.Domain.Settings;

namespace Zumingtalk.Domain.Services;

public interface ISettingsRepository
{
    Task<AppSettings> GetAsync(CancellationToken cancellationToken);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken);

    Task<AliyunCredentialSettings> GetAliyunCredentialsAsync(CancellationToken cancellationToken);

    Task SaveAliyunCredentialsAsync(AliyunCredentialSettings credentials, CancellationToken cancellationToken);

    Task<BailianCredentialSettings> GetBailianCredentialsAsync(CancellationToken cancellationToken);

    Task SaveBailianCredentialsAsync(BailianCredentialSettings credentials, CancellationToken cancellationToken);

    Task<VolcengineCredentialSettings> GetVolcengineCredentialsAsync(CancellationToken cancellationToken);

    Task SaveVolcengineCredentialsAsync(VolcengineCredentialSettings credentials, CancellationToken cancellationToken);

    Task<ZumingtalkCloudCredentialSettings> GetZumingtalkCloudCredentialsAsync(CancellationToken cancellationToken);

    Task SaveZumingtalkCloudCredentialsAsync(ZumingtalkCloudCredentialSettings credentials, CancellationToken cancellationToken);
}
