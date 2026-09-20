using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using VendingMachines.Api.Models;

namespace VendingMachines.Api.Services;

public sealed record AccessTokenResult(string Token, DateTime ExpiresAt);

public sealed class AuthTokenService
{
    private readonly IConfiguration _configuration;
    private readonly AuthOptions _options;
    private readonly TimeProvider _timeProvider;

    public AuthTokenService(
        IConfiguration configuration,
        IOptions<AuthOptions> options,
        TimeProvider timeProvider)
    {
        _configuration = configuration;
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public AccessTokenResult CreateAccessToken(User user)
    {
        var jwtKey = _configuration["Jwt:Key"]
            ?? throw new InvalidOperationException("A configuração Jwt:Key é obrigatória.");
        var issuer = _configuration["Jwt:Issuer"] ?? "VendingMachinesApi";
        var audience = _configuration["Jwt:Audience"] ?? "VendingMachinesApp";
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var expiresAt = now.AddMinutes(_options.AccessTokenMinutes);

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Name),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Role, user.Role),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Iat,
                new DateTimeOffset(now).ToUnixTimeSeconds().ToString(),
                ClaimValueTypes.Integer64)
        };

        if (user.CompanyId.HasValue)
        {
            claims.Add(new Claim("company_id", user.CompanyId.Value.ToString()));
            if (user.Company != null)
            {
                claims.Add(new Claim("company_name", user.Company.Name));
            }
        }

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: now,
            expires: expiresAt,
            signingCredentials: credentials);

        return new AccessTokenResult(new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}
