using Zumingtalk.Domain.Settings;

namespace Zumingtalk.Domain.Services;

public interface IAsrProviderFactory
{
    IAsrProvider Create(BailianCredentialSettings credentials, bool semanticPunctuationEnabled);

    IAsrProvider Create(VolcengineCredentialSettings credentials, bool semanticPunctuationEnabled);
}
