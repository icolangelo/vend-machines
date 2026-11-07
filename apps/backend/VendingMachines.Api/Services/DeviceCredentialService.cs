using System.Security.Cryptography;
using System.Text;

namespace VendingMachines.Api.Services;

public static class DeviceCredentialService
{
    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return "vmc_" + Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static string Hash(string credential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential.Trim())));
    }

    public static bool Verify(string credential, string? expectedHash)
    {
        if (string.IsNullOrWhiteSpace(credential) || string.IsNullOrWhiteSpace(expectedHash))
            return false;

        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(credential.Trim()));
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(expectedHash);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static bool Verify(string credential, string? expectedHash, DateTime? revokedAt) =>
        !revokedAt.HasValue && Verify(credential, expectedHash);
}
