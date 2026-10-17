using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;

namespace VendingMachines.Api.Middlewares;

public sealed class UserRoleValidationMiddleware
{
    private readonly RequestDelegate _next;

    public UserRoleValidationMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, AppDbContext db)
    {
        var allowAnonymous = context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() != null;
        if (!allowAnonymous && context.User.Identity?.IsAuthenticated == true)
        {
            var userIdValue = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            var tokenRole = context.User.FindFirstValue(ClaimTypes.Role);
            var credentialVersionClaim = context.User.FindFirstValue("credential_version");
            var tokenCredentialVersion = 0;
            if (credentialVersionClaim != null &&
                !int.TryParse(credentialVersionClaim, out tokenCredentialVersion))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            if (!Guid.TryParse(userIdValue, out var userId) || string.IsNullOrWhiteSpace(tokenRole))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var persistedUser = await db.Users.AsNoTracking()
                .Where(user => user.Id == userId)
                .Select(user => new
                {
                    user.Role,
                    user.IsActive,
                    user.RequiresEmailVerification,
                    user.EmailVerifiedAt,
                    user.CredentialVersion,
                    CompanyActivationStatus = user.CompanyId == null
                        ? CompanyActivationStatuses.Active
                        : db.Companies.Where(company => company.Id == user.CompanyId)
                            .Select(company => company.ActivationStatus)
                            .SingleOrDefault()
                })
                .SingleOrDefaultAsync(context.RequestAborted);

            if (persistedUser is not { IsActive: true } ||
                (persistedUser.RequiresEmailVerification && !persistedUser.EmailVerifiedAt.HasValue) ||
                persistedUser.CompanyActivationStatus != CompanyActivationStatuses.Active ||
                !string.Equals(persistedUser.Role, tokenRole, StringComparison.Ordinal) ||
                persistedUser.CredentialVersion != tokenCredentialVersion)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
        }

        await _next(context);
    }
}
