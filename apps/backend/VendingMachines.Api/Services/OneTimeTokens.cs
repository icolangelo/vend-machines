using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace VendingMachines.Api.Services;

public static class OneTimeTokens
{
    public static string Create() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
