using Microsoft.AspNetCore.Mvc;
using VendingMachines.Api.Middlewares;

namespace VendingMachines.Api.Controllers;

/// <summary>
/// Base controller that provides common helpers for all API controllers,
/// including SuperAdmin company impersonation support.
/// </summary>
public abstract class BaseApiController : ControllerBase
{
    /// <summary>
    /// Returns the effective CompanyId for the current request.
    /// If a SuperAdmin is impersonating another company, returns that company's Id.
    /// Otherwise, returns the authenticated user's own CompanyId.
    /// </summary>
    protected bool TryGetEffectiveCompanyId(out Guid companyId)
    {
        // Check for active impersonation (SuperAdmin only, validated by middleware)
        var impersonated = User.FindFirst(ImpersonationMiddleware.ImpersonatedCompanyIdClaim)?.Value;
        if (!string.IsNullOrEmpty(impersonated) && Guid.TryParse(impersonated, out companyId))
        {
            return true;
        }

        // Fall back to the authenticated user's own company_id claim
        var companyIdClaim = User.FindFirst("company_id")?.Value;
        return Guid.TryParse(companyIdClaim, out companyId);
    }

    /// <summary>
    /// Returns whether the current request is running in impersonation mode.
    /// </summary>
    protected bool IsImpersonating =>
        !string.IsNullOrEmpty(User.FindFirst(ImpersonationMiddleware.ImpersonatedCompanyIdClaim)?.Value);
}
