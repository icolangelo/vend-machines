using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using VendingMachines.Api.Services;

namespace VendingMachines.Api.Middlewares;

public class ImpersonationMiddleware
{
    private const string ImpersonationHeader = "X-Impersonate-Company-Id";
    public const string ImpersonatedCompanyIdClaim = "impersonated_company_id";

    private readonly RequestDelegate _next;

    public ImpersonationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var impersonateHeader = context.Request.Headers[ImpersonationHeader].FirstOrDefault();
        var principal = context.User;

        foreach (var identity in principal.Identities)
        {
            foreach (var existing in identity.FindAll(ImpersonatedCompanyIdClaim).ToArray())
                identity.RemoveClaim(existing);
        }

        if (!string.IsNullOrWhiteSpace(impersonateHeader) &&
            Guid.TryParse(impersonateHeader, out var impersonatedCompanyId) &&
            principal.IsInRole(AppRoles.PlatformAdmin))
        {
            var identity = principal.Identities.FirstOrDefault(candidate =>
                candidate.HasClaim(candidate.RoleClaimType, AppRoles.PlatformAdmin));
            if (identity is not null)
                identity.AddClaim(new Claim(ImpersonatedCompanyIdClaim, impersonatedCompanyId.ToString()));
        }

        await _next(context);
    }
}
