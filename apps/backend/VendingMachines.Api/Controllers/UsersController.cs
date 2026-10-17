using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Security.Claims;
using System.Net.Mail;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;

namespace VendingMachines.Api.Controllers;

[Authorize(Roles = AppRoles.Admin + "," + AppRoles.PlatformAdmin)]
[ApiController]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IAuditLogService? _audit;
    private readonly TelemetryPanelHub? _panelHub;
    private readonly IAccountEmailSender? _accountEmailSender;

    public UsersController(
        AppDbContext context,
        IAuditLogService? audit = null,
        TelemetryPanelHub? panelHub = null,
        IAccountEmailSender? accountEmailSender = null)
    {
        _context = context;
        _audit = audit;
        _panelHub = panelHub;
        _accountEmailSender = accountEmailSender;
    }

    [HttpGet]
    public async Task<IActionResult> GetPaginatedUsers(
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? searchTerm = null)
    {
        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1) pageSize = 10;
        if (pageSize > 100) pageSize = 100; // Limite máximo para proteção

        var isPlatformAdmin = User.IsInRole(AppRoles.PlatformAdmin);
        var query = _context.Users.AsQueryable();

        if (!isPlatformAdmin)
        {
            var companyIdValue = User.FindFirst("company_id")?.Value;
            if (!Guid.TryParse(companyIdValue, out var companyId))
            {
                return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
            }

            query = query.Where(u => u.CompanyId == companyId);
        }

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var search = searchTerm.ToLower();
            query = query.Where(u => u.Name.ToLower().Contains(search) || u.Email.ToLower().Contains(search));
        }

        var totalItems = await query.CountAsync();
        var totalPages = (int)Math.Ceiling((double)totalItems / pageSize);

        var items = await query
            .OrderBy(u => u.Name) // Ordenado alfabeticamente por nome
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new UserDto
            {
                Id = u.Id,
                Name = u.Name,
                Email = u.Email,
                Cpf = u.Cpf,
                Role = u.Role,
                IsActive = u.IsActive,
                CompanyId = u.CompanyId,
                CompanyName = u.Company != null ? u.Company.Name : null,
                CreatedAt = u.CreatedAt
            })
            .ToListAsync();

        return Ok(new PaginatedResult<UserDto>
        {
            Items = items,
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalItems = totalItems,
            TotalPages = totalPages
        });
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost("invitations")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> CreateInvitation(
        [FromBody] CreateUserInvitationRequest request,
        CancellationToken cancellationToken)
    {
        var actorId = Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var parsedActorId)
            ? parsedActorId
            : (Guid?)null;
        var companyId = Guid.TryParse(User.FindFirst("company_id")?.Value, out var parsedCompanyId)
            ? parsedCompanyId
            : (Guid?)null;
        if (!actorId.HasValue || !companyId.HasValue)
            return BadRequest(new { message = "O usuário não está associado a uma empresa." });

        var email = request.Email?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email) ||
            email.Length > 320 ||
            !MailAddress.TryCreate(email, out var parsedEmail) ||
            !string.Equals(parsedEmail!.Address, email, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Informe um endereço de e-mail válido." });

        var role = request.Role?.Trim();
        if (role is not (AppRoles.Admin or AppRoles.Operator))
            return BadRequest(new { message = "O papel do convite deve ser Admin ou Operator." });

        if (await _context.Users.AnyAsync(
            user => user.NormalizedEmail == RegistrationIdentity.NormalizeEmail(email),
            cancellationToken))
            return Conflict(new { message = "Já existe uma conta cadastrada com este e-mail." });

        var now = DateTime.UtcNow;
        var invitation = await _context.UserInvitations.FirstOrDefaultAsync(
            item => item.CompanyId == companyId && item.Email == email && item.AcceptedAt == null,
            cancellationToken);
        var isRotation = invitation != null;
        invitation ??= new UserInvitation
        {
            CompanyId = companyId.Value,
            CreatedByUserId = actorId.Value,
            Email = email
        };

        var rawToken = OneTimeTokens.Create();
        invitation.Role = role;
        invitation.TokenHash = OneTimeTokens.Hash(rawToken);
        invitation.CreatedByUserId = actorId.Value;
        invitation.CreatedAt = now;
        invitation.ExpiresAt = now.AddDays(7);

        if (!isRotation)
            _context.UserInvitations.Add(invitation);
        await _context.SaveChangesAsync(cancellationToken);

        if (_audit != null)
        {
            await _audit.RecordAsync(
                isRotation ? "user.invitation_rotated" : "user.invitation_created",
                "UserInvitation",
                invitation.Id.ToString(),
                companyId,
                new { role = invitation.Role, expiresAt = invitation.ExpiresAt },
                cancellationToken);
        }

        var emailSent = _accountEmailSender != null && await _accountEmailSender.SendInvitationAsync(
            invitation.Email,
            invitation.Role,
            rawToken,
            invitation.ExpiresAt,
            cancellationToken);

        return Ok(new
        {
            invitationId = invitation.Id,
            invitationToken = emailSent ? null : rawToken,
            emailSent,
            expiresAt = invitation.ExpiresAt,
            rotated = isRotation
        });
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPut("invitations/{invitationId:guid}/revoke")]
    public async Task<IActionResult> RevokeInvitation(Guid invitationId, CancellationToken cancellationToken)
    {
        var companyId = Guid.TryParse(User.FindFirst("company_id")?.Value, out var parsedCompanyId)
            ? parsedCompanyId
            : (Guid?)null;
        if (!companyId.HasValue)
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });

        var invitation = await _context.UserInvitations.FirstOrDefaultAsync(
            item => item.Id == invitationId && item.CompanyId == companyId && item.AcceptedAt == null,
            cancellationToken);
        if (invitation == null)
            return NotFound(new { message = "Convite pendente não encontrado." });

        invitation.ExpiresAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        if (_audit != null)
        {
            await _audit.RecordAsync(
                "user.invitation_revoked",
                "UserInvitation",
                invitation.Id.ToString(),
                companyId,
                cancellationToken: cancellationToken);
        }

        return NoContent();
    }

    [Authorize(Roles = AppRoles.CompanyAdminOrPlatformAdmin)]
    [HttpPost("{userId:guid}/password-reset")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> CreatePasswordResetLink(Guid userId, CancellationToken cancellationToken)
    {
        var actorId = Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var parsedActorId)
            ? parsedActorId
            : (Guid?)null;
        if (!actorId.HasValue)
            return Unauthorized();
        if (actorId.Value == userId)
            return Conflict(new { message = "Peça a outro administrador para gerar seu link de recuperação." });

        var isPlatformAdmin = User.IsInRole(AppRoles.PlatformAdmin);
        var companyId = Guid.TryParse(User.FindFirst("company_id")?.Value, out var parsedCompanyId)
            ? parsedCompanyId
            : (Guid?)null;
        if (!isPlatformAdmin && !companyId.HasValue)
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });

        var target = await _context.Users.FirstOrDefaultAsync(
            user => user.Id == userId && (isPlatformAdmin || user.CompanyId == companyId),
            cancellationToken);
        if (target == null)
            return NotFound(new { message = "Usuário não encontrado." });
        if (!isPlatformAdmin && target.Role == AppRoles.PlatformAdmin)
            return Forbid();
        if (!target.IsActive)
            return Conflict(new { message = "Reative a conta antes de gerar um link de recuperação." });

        var now = DateTime.UtcNow;
        await _context.PasswordResetTokens
            .Where(token => token.UserId == target.Id && token.UsedAt == null && token.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.RevokedAt, now), cancellationToken);

        var rawToken = OneTimeTokens.Create();
        var resetToken = new PasswordResetToken
        {
            UserId = target.Id,
            CreatedByUserId = actorId.Value,
            TokenHash = OneTimeTokens.Hash(rawToken),
            CreatedAt = now,
            ExpiresAt = now.AddHours(1)
        };
        _context.PasswordResetTokens.Add(resetToken);
        await _context.SaveChangesAsync(cancellationToken);

        if (_audit != null)
        {
            await _audit.RecordAsync(
                "user.password_reset_issued",
                "User",
                target.Id.ToString(),
                target.CompanyId,
                new { expiresAt = resetToken.ExpiresAt },
                cancellationToken);
        }

        var emailSent = _accountEmailSender != null && await _accountEmailSender.SendPasswordResetAsync(
            target.Email,
            rawToken,
            resetToken.ExpiresAt,
            cancellationToken);

        return Ok(new { resetToken = emailSent ? null : rawToken, emailSent, expiresAt = resetToken.ExpiresAt });
    }

    [Authorize(Roles = AppRoles.CompanyAdminOrPlatformAdmin)]
    [HttpPut("{userId:guid}/role")]
    public async Task<IActionResult> UpdateRole(
        Guid userId,
        [FromBody] UpdateUserRoleRequest request,
        CancellationToken cancellationToken)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Role))
            return BadRequest(new { message = "Informe o papel que deseja atribuir." });

        var requestedRole = request.Role.Trim();
        if (requestedRole is not (AppRoles.Admin or AppRoles.PlatformAdmin or AppRoles.Operator))
            return BadRequest(new { message = "O papel informado não é válido." });

        var actorId = Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var parsedActorId)
            ? parsedActorId
            : (Guid?)null;
        if (!actorId.HasValue)
            return Unauthorized();
        if (actorId.Value == userId)
            return Conflict(new { message = "Você não pode alterar o próprio papel." });

        var isPlatformAdmin = User.IsInRole(AppRoles.PlatformAdmin);
        var companyId = Guid.TryParse(User.FindFirst("company_id")?.Value, out var parsedCompanyId)
            ? parsedCompanyId
            : (Guid?)null;
        if (!isPlatformAdmin && !companyId.HasValue)
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });

        var target = await _context.Users.FirstOrDefaultAsync(
            user => user.Id == userId && (isPlatformAdmin || user.CompanyId == companyId),
            cancellationToken);
        if (target == null)
            return NotFound(new { message = "Usuário não encontrado." });

        if (!isPlatformAdmin &&
            (target.Role == AppRoles.PlatformAdmin || requestedRole == AppRoles.PlatformAdmin))
        {
            return Forbid();
        }

        var previousRole = target.Role;
        if (previousRole == requestedRole)
            return Ok(new { userId, role = target.Role, changed = false });

        target.Role = requestedRole;
        target.CredentialVersion++;
        await _context.SaveChangesAsync(cancellationToken);

        var now = DateTime.UtcNow;
        await _context.RefreshSessions
            .Where(session => session.UserId == target.Id && session.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(session => session.RevokedAt, now), cancellationToken);

        if (_audit != null)
        {
            await _audit.RecordAsync(
                "user.role_changed",
                "User",
                target.Id.ToString(),
                target.CompanyId,
                new { previousRole, newRole = requestedRole },
                cancellationToken);
        }

        return Ok(new
        {
            userId,
            role = target.Role,
            changed = true,
            sessionsRevoked = true,
            message = "Papel atualizado. O usuário deverá entrar novamente."
        });
    }

    [Authorize(Roles = AppRoles.CompanyAdminOrPlatformAdmin)]
    [HttpPut("{userId:guid}/active")]
    public async Task<IActionResult> UpdateActiveStatus(
        Guid userId,
        [FromBody] UpdateUserActiveStatusRequest request,
        CancellationToken cancellationToken)
    {
        var actorId = Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var parsedActorId)
            ? parsedActorId
            : (Guid?)null;
        if (!actorId.HasValue)
            return Unauthorized();
        if (actorId.Value == userId)
            return Conflict(new { message = "Você não pode desativar ou reativar a própria conta." });

        var isPlatformAdmin = User.IsInRole(AppRoles.PlatformAdmin);
        var companyId = Guid.TryParse(User.FindFirst("company_id")?.Value, out var parsedCompanyId)
            ? parsedCompanyId
            : (Guid?)null;
        if (!isPlatformAdmin && !companyId.HasValue)
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });

        var target = await _context.Users.FirstOrDefaultAsync(
            user => user.Id == userId && (isPlatformAdmin || user.CompanyId == companyId),
            cancellationToken);
        if (target == null)
            return NotFound(new { message = "Usuário não encontrado." });

        if (!isPlatformAdmin && target.Role == AppRoles.PlatformAdmin)
            return Forbid();

        if (request.IsActive && target.RequiresEmailVerification && !target.EmailVerifiedAt.HasValue)
            return Conflict(new { message = "Confirme o endereço de e-mail antes de ativar esta conta." });

        if (target.IsActive == request.IsActive)
            return Ok(new { userId, isActive = target.IsActive, changed = false });

        if (!isPlatformAdmin && !request.IsActive && target.Role == AppRoles.Admin)
        {
            var activeAdmins = await _context.Users.CountAsync(
                user => user.CompanyId == target.CompanyId &&
                        user.Role == AppRoles.Admin &&
                        user.IsActive &&
                        user.Id != target.Id,
                cancellationToken);
            if (activeAdmins == 0)
                return Conflict(new { message = "A empresa precisa manter pelo menos um administrador ativo." });
        }

        target.IsActive = request.IsActive;
        if (!target.IsActive)
            target.CredentialVersion++;
        await _context.SaveChangesAsync(cancellationToken);

        if (!target.IsActive)
        {
            var now = DateTime.UtcNow;
            await _context.RefreshSessions
                .Where(session => session.UserId == target.Id && session.RevokedAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(session => session.RevokedAt, now), cancellationToken);
            if (_panelHub != null)
                await _panelHub.DisconnectUserAsync(target.Id);
        }

        if (_audit != null)
        {
            await _audit.RecordAsync(
                target.IsActive ? "user.reactivated" : "user.deactivated",
                "User",
                target.Id.ToString(),
                target.CompanyId,
                new { isActive = target.IsActive },
                cancellationToken);
        }

        return Ok(new
        {
            userId,
            isActive = target.IsActive,
            changed = true,
            sessionsRevoked = !target.IsActive
        });
    }
}

public sealed class UpdateUserRoleRequest
{
    public string Role { get; set; } = string.Empty;
}

public sealed class UpdateUserActiveStatusRequest
{
    public bool IsActive { get; set; }
}

public sealed class CreateUserInvitationRequest
{
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = AppRoles.Operator;
}

public class UserDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Cpf { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public Guid? CompanyId { get; set; }
    public string? CompanyName { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class PaginatedResult<T>
{
    public List<T> Items { get; set; } = new();
    public int PageNumber { get; set; }
    public int PageSize { get; set; }
    public int TotalItems { get; set; }
    public int TotalPages { get; set; }
}
