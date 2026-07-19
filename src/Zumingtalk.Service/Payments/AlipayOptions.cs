namespace Zumingtalk.Service.Payments;

public sealed class AlipayOptions
{
    public const string SectionName = "Alipay";

    public string AppId { get; set; } = string.Empty;
    public string SellerId { get; set; } = string.Empty;
    public string Gateway { get; set; } = "https://openapi.alipaydev.com/gateway.do";
    public string NotifyUrl { get; set; } = string.Empty;
    public string ReturnUrl { get; set; } = string.Empty;
    public string MerchantPrivateKeyPem { get; set; } = string.Empty;
    public string MerchantPrivateKeyPath { get; set; } = string.Empty;
    public string AlipayPublicKeyPem { get; set; } = string.Empty;
    public string AlipayPublicKeyPath { get; set; } = string.Empty;
    public int ProMonthAmountFen { get; set; } = 2000;
    public int AddOnAmountFen { get; set; } = 2000;

    public string GetMerchantPrivateKey() => ReadKey(MerchantPrivateKeyPem, MerchantPrivateKeyPath, "merchant private key");

    public string GetAlipayPublicKey() => ReadKey(AlipayPublicKeyPem, AlipayPublicKeyPath, "Alipay public key");

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(AppId) || string.IsNullOrWhiteSpace(SellerId))
        {
            throw new InvalidOperationException("Alipay AppId and SellerId must be configured through environment variables or local secrets.");
        }

        if (!Uri.TryCreate(Gateway, UriKind.Absolute, out var gateway) || gateway.Scheme != Uri.UriSchemeHttps ||
            !Uri.TryCreate(NotifyUrl, UriKind.Absolute, out var notify) || notify.Scheme != Uri.UriSchemeHttps ||
            !Uri.TryCreate(ReturnUrl, UriKind.Absolute, out var returnUrl) || returnUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Alipay gateway, notify URL and return URL must use absolute HTTPS URLs.");
        }

        if (ProMonthAmountFen <= 0 || AddOnAmountFen <= 0)
        {
            throw new InvalidOperationException("Alipay product prices must be positive server-side values.");
        }

        _ = GetMerchantPrivateKey();
        _ = GetAlipayPublicKey();
    }

    private static string ReadKey(string inlineValue, string path, string label)
    {
        if (!string.IsNullOrWhiteSpace(inlineValue))
        {
            return inlineValue;
        }

        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            return File.ReadAllText(path);
        }

        throw new InvalidOperationException($"Alipay {label} is not configured.");
    }
}
