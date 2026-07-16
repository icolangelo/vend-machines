using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;

namespace VendingMachines.Api.Middlewares;

public class ImpersonationMiddleware
{
    private const string ImpersonationHeader = "X-Impersonate-Company-Id";
    private const string SuperAdminEmail = "dev.ivan@gmail.com";
    public const string ImpersonatedCompanyIdClaim = "impersonated_company_id";

    private readonly RequestDelegate _next;

    public ImpersonationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var impersonateHeader = context.Request.Headers[ImpersonationHeader].FirstOrDefault();

        if (!string.IsNullOrEmpty(impersonateHeader) && Guid.TryParse(impersonateHeader, out var impersonatedCompanyId))
        {
            var email = context.User?.FindFirst(ClaimTypes.Email)?.Value
                        ?? context.User?.FindFirst(JwtRegisteredClaimNames.Email)?.Value
                        ?? context.User?.FindFirst("email")?.Value;

            if (string.Equals(email, SuperAdminEmail, StringComparison.OrdinalIgnoreCase))
            {
                var identity = context.User!.Identity as ClaimsIdentity;
                if (identity != null)
                {
                    var existing = identity.FindFirst(ImpersonatedCompanyIdClaim);
                    if (existing != null) identity.RemoveClaim(existing);

                    identity.AddClaim(new Claim(ImpersonatedCompanyIdClaim, impersonatedCompanyId.ToString()));
                }
            }
        }

        await _next(context);
    }
}
