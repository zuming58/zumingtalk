using Zumingtalk.Domain.Settings;

namespace Zumingtalk.Domain.Services;

public interface IAsrProviderFactory
{
    IAsrProvider Create(AliyunCredentialSettings credentials, bool oralSmoothingEnabled);
}
