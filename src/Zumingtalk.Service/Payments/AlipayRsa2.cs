using System.Security.Cryptography;
using System.Text;

namespace Zumingtalk.Service.Payments;

public static class AlipayRsa2
{
    public static string BuildCanonicalString(IEnumerable<KeyValuePair<string, string>> parameters) =>
        string.Join("&", parameters
            .Where(value => !string.Equals(value.Key, "sign", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(value.Key, "sign_type", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrEmpty(value.Value))
            .OrderBy(value => value.Key, StringComparer.Ordinal)
            .Select(value => $"{value.Key}={value.Value}"));

    public static string Sign(IEnumerable<KeyValuePair<string, string>> parameters, string privateKeyPem) =>
        SignContent(BuildCanonicalString(parameters), privateKeyPem);

    public static string SignContent(string content, string privateKeyPem)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(NormalizePem(privateKeyPem, isPrivate: true));
        return Convert.ToBase64String(rsa.SignData(Encoding.UTF8.GetBytes(content), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    public static bool Verify(IEnumerable<KeyValuePair<string, string>> parameters, string signature, string publicKeyPem) =>
        VerifyContent(BuildCanonicalString(parameters), signature, publicKeyPem);

    public static bool VerifyContent(string content, string signature, string publicKeyPem)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(NormalizePem(publicKeyPem, isPrivate: false));
            return rsa.VerifyData(Encoding.UTF8.GetBytes(content), Convert.FromBase64String(signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static string NormalizePem(string value, bool isPrivate)
    {
        var trimmed = value.Trim();
        if (trimmed.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var label = isPrivate ? "PRIVATE KEY" : "PUBLIC KEY";
        return $"-----BEGIN {label}-----\n{trimmed}\n-----END {label}-----";
    }
}
