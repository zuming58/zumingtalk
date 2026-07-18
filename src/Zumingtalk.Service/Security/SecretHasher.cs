using System.Security.Cryptography;
using System.Text;

namespace Zumingtalk.Service.Security;

public static class SecretHasher
{
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string GenerateDeviceToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .Replace('+', '-')
        .Replace('/', '_')
        .TrimEnd('=');

    public static bool FixedTimeEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
}
