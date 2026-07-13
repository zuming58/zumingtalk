using System.Globalization;
using Aliyun.Acs.Core;
using Aliyun.Acs.Core.Http;
using Aliyun.Acs.Core.Profile;
using Newtonsoft.Json.Linq;
using Zumingtalk.Domain.Settings;

namespace Zumingtalk.Infrastructure.Asr;

public sealed class AliyunTokenProvider
{
    private readonly IClientProfile clientProfile;
    private string? cachedToken;
    private DateTimeOffset expiresAt;

    public AliyunTokenProvider(AliyunCredentialSettings credentials)
    {
        Credentials = credentials;
        clientProfile = DefaultProfile.GetProfile(credentials.RegionId, credentials.AccessKeyId, credentials.AccessKeySecret);
    }

    public AliyunCredentialSettings Credentials { get; }

    public async Task<string> GetTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        if (!forceRefresh && !string.IsNullOrWhiteSpace(cachedToken) && expiresAt > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return cachedToken;
        }

        if (string.IsNullOrWhiteSpace(Credentials.AppKey) ||
            string.IsNullOrWhiteSpace(Credentials.AccessKeyId) ||
            string.IsNullOrWhiteSpace(Credentials.AccessKeySecret))
        {
            throw new InvalidOperationException("Aliyun ASR credentials are not configured.");
        }

        var client = new DefaultAcsClient(clientProfile);
        var request = new CommonRequest
        {
            Domain = $"nls-meta.{Credentials.RegionId}.aliyuncs.com",
            Version = "2019-02-28",
            Action = "CreateToken",
            Method = MethodType.POST
        };

        var response = await Task.Run(() => client.GetCommonResponse(request), cancellationToken);
        var payload = JObject.Parse(response.Data);
        var tokenNode = payload["Token"];
        var token = tokenNode?["Id"]?.ToString();
        var expireTime = tokenNode?["ExpireTime"]?.ToString();
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Aliyun CreateToken response did not contain a token.");
        }

        cachedToken = token;
        expiresAt = long.TryParse(expireTime, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epochSeconds)
            ? DateTimeOffset.FromUnixTimeSeconds(epochSeconds)
            : DateTimeOffset.UtcNow.AddHours(1);
        return cachedToken;
    }
}
