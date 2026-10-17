using System;
using System.Security.Claims;
using System.Data;
using System.Net.Mail;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;

namespace VendingMachines.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    // Mantido apenas para impedir que o cadastro público reutilize a identidade
    // reservada do administrador inicial.
    private const string ReservedPlatformAdminEmail = "dev.ivan@gmail.com";

    private readonly AppDbContext _context;
    private readonly AuthTokenService _tokenService;
    private readonly RefreshSessionService _refreshSessions;
    private readonly AuthCookieService _cookieService;
    private readonly IAuditLogService? _audit;
    private readonly TelemetryPanelHub? _panelHub;
    private readonly ILogger<AuthController>? _logger;
    private readonly IAccountEmailSender? _accountEmailSender;

    public AuthController(
        AppDbContext context,
        AuthTokenService tokenService,
        RefreshSessionService refreshSessions,
        AuthCookieService cookieService,
        IAuditLogService? audit = null,
        TelemetryPanelHub? panelHub = null,
        ILogger<AuthController>? logger = null,
        IAccountEmailSender? accountEmailSender = null)
    {
        _context = context;
        _tokenService = tokenService;
        _refreshSessions = refreshSessions;
        _cookieService = cookieService;
        _audit = audit;
        _panelHub = panelHub;
        _logger = logger;
        _accountEmailSender = accountEmailSender;
    }

    [HttpPost("login")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { message = "E-mail e senha são obrigatórios." });
        }

        var normalizedEmail = RegistrationIdentity.NormalizeEmail(request.Email);
        var user = await _context.Users.Include(u => u.Company)
            .FirstOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail);
        if (user == null)
        {
            await RecordAuditAsync("auth.login_failed", "Auth", metadata: new { reason = "unknown_user" });
            return Unauthorized(new { message = "Credenciais inválidas. E-mail ou senha incorretos." });
        }

        var hasher = new PasswordHasher<User>();
        var result = hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);

        if (result == PasswordVerificationResult.Failed)
        {
            await RecordAuditAsync("auth.login_failed", "User", user.Id.ToString(), user.CompanyId, new { reason = "invalid_password" });
            return Unauthorized(new { message = "Credenciais inválidas. E-mail ou senha incorretos." });
        }

        if (user.RequiresEmailVerification && !user.EmailVerifiedAt.HasValue)
        {
            await RecordAuditAsync("auth.login_failed", "User", user.Id.ToString(), user.CompanyId, new { reason = "email_unverified" });
            return Unauthorized(new { message = "Confirme seu endereço de e-mail antes de entrar." });
        }

        if (user.Company != null && user.Company.ActivationStatus != CompanyActivationStatuses.Active)
        {
            await RecordAuditAsync("auth.login_failed", "User", user.Id.ToString(), user.CompanyId, new { reason = "company_not_active" });
            return Unauthorized(new { message = "A conta da empresa ainda não está liberada para acesso." });
        }

        if (!user.IsActive)
        {
            await RecordAuditAsync("auth.login_failed", "User", user.Id.ToString(), user.CompanyId, new { reason = "account_inactive" });
            return Unauthorized(new { message = "Credenciais inválidas. E-mail ou senha incorretos." });
        }

        var session = await CreateSessionAsync(user);
        await RecordAuditAsync("auth.login_succeeded", "User", user.Id.ToString(), user.CompanyId);
        return Ok(session);
    }

    [HttpPost("register")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        if (request == null || 
            string.IsNullOrWhiteSpace(request.Name) || 
            string.IsNullOrWhiteSpace(request.Email) || 
            string.IsNullOrWhiteSpace(request.Password) ||
            string.IsNullOrWhiteSpace(request.Cpf) ||
            string.IsNullOrWhiteSpace(request.CompanyName) ||
            string.IsNullOrWhiteSpace(request.CompanyCnpj))
        {
            return BadRequest(new { message = "Todos os campos são obrigatórios." });
        }

        if (request.Password.Length < 12 || request.Password.Length > 256)
            return BadRequest(new { message = "A senha deve ter entre 12 e 256 caracteres." });

        if (!request.AcceptedPrivacyPolicy)
        {
            return BadRequest(new { message = "Você precisa aceitar a Política de Privacidade para se cadastrar." });
        }

        if (string.Equals(request.Email.Trim(), ReservedPlatformAdminEmail, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { message = "Este e-mail é reservado para a administração da plataforma." });
        }

        var normalizedEmail = RegistrationIdentity.NormalizeEmail(request.Email);
        if (normalizedEmail.Length > 320 ||
            !MailAddress.TryCreate(normalizedEmail, out var parsedEmail) ||
            !string.Equals(parsedEmail!.Address, normalizedEmail, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Informe um endereço de e-mail válido." });

        var normalizedCpf = RegistrationIdentity.NormalizeCpf(request.Cpf);
        if (!RegistrationIdentity.IsValidCpf(normalizedCpf))
            return BadRequest(new { message = "Informe um CPF válido." });

        var normalizedCnpj = RegistrationIdentity.NormalizeCnpj(request.CompanyCnpj);
        if (!RegistrationIdentity.IsValidCnpj(normalizedCnpj))
            return BadRequest(new { message = "Informe um CNPJ válido." });

        request.Email = normalizedEmail;

        // Validar e-mail único
        if (await _context.Users.AnyAsync(u => u.NormalizedEmail == normalizedEmail))
        {
            return Conflict(new { message = "Este e-mail já está sendo utilizado por outro usuário." });
        }

        // Validar CPF único
        if (await _context.Users.AnyAsync(u => u.NormalizedCpf == normalizedCpf))
        {
            return Conflict(new { message = "Este CPF já está cadastrado no sistema." });
        }

        // Validar CNPJ único
        if (await _context.Companies.AnyAsync(c => c.NormalizedCnpj == normalizedCnpj))
        {
            return Conflict(new { message = "Este CNPJ já está cadastrado no sistema." });
        }

        using var transaction = await _context.Database.BeginTransactionAsync();
        try
        {
            // Criar a empresa primeiro
            var company = new Company
            {
                Name = request.CompanyName,
                Cnpj = request.CompanyCnpj.Trim(),
                NormalizedCnpj = normalizedCnpj,
                CreatedAt = DateTime.UtcNow,
                ActivationStatus = CompanyActivationStatuses.Pending
            };
            _context.Companies.Add(company);
            await _context.SaveChangesAsync();

            // Criar o usuário
            var user = new User
            {
                Name = request.Name,
                Email = request.Email,
                NormalizedEmail = normalizedEmail,
                Cpf = request.Cpf,
                NormalizedCpf = normalizedCpf,
                Role = AppRoles.Admin, // O criador original da empresa é o Administrador
                IsActive = false,
                CompanyId = company.Id,
                CreatedAt = DateTime.UtcNow,
                AcceptedPrivacyPolicy = true,
                PrivacyPolicyAcceptedAt = DateTime.UtcNow,
                RequiresEmailVerification = true
            };

            var rawVerificationToken = OneTimeTokens.Create();
            var verificationExpiresAt = DateTime.UtcNow.AddHours(24);
            user.EmailVerificationTokenHash = OneTimeTokens.Hash(rawVerificationToken);
            user.EmailVerificationTokenExpiresAt = verificationExpiresAt;

            var hasher = new PasswordHasher<User>();
            user.PasswordHash = hasher.HashPassword(user, request.Password);

            _context.Users.Add(user);
            await _context.SaveChangesAsync();

            // Definir o ID do criador da empresa
            company.CreatedByUserId = user.Id;
            await _context.SaveChangesAsync();

            user.Company = company;
            await transaction.CommitAsync();
            await RecordAuditAsync(
                "auth.company_registration_submitted",
                "Company",
                company.Id.ToString(),
                company.Id,
                new { activationStatus = CompanyActivationStatuses.Pending });
            var verificationEmailSent = _accountEmailSender != null && await _accountEmailSender.SendEmailVerificationAsync(
                user.Email,
                rawVerificationToken,
                verificationExpiresAt,
                HttpContext.RequestAborted);
            await RecordAuditAsync(
                "auth.email_verification_issued",
                "User",
                user.Id.ToString(),
                company.Id,
                new { emailSent = verificationEmailSent });
            return Accepted(new
            {
                status = CompanyActivationStatuses.Pending,
                verificationEmailSent,
                message = verificationEmailSent
                    ? "Cadastro recebido. Confirme seu e-mail pelo link enviado. Depois, a equipe da plataforma analisará os dados da empresa."
                    : "Cadastro recebido, mas não foi possível enviar o link de confirmação. Tente solicitar o envio novamente ou contate o suporte."
            });
        }
        catch (Exception ex)
        {
            try
            {
                await transaction.RollbackAsync();
            }
            catch (InvalidOperationException)
            {
                // A transação pode já ter sido confirmada antes de uma falha ao criar a sessão.
            }
            if (ex is DbUpdateException updateException && IsUniqueConstraintViolation(updateException))
                return Conflict(new { message = "E-mail, CPF ou CNPJ já cadastrado." });

            _logger?.LogError(
                "Cadastro público falhou. TraceId {TraceId}; tipo de falha {ExceptionType}.",
                HttpContext.TraceIdentifier,
                ex.GetType().Name);
            return StatusCode(500, new { message = "Não foi possível concluir o cadastro. Tente novamente ou contate o suporte." });
        }
    }

    [AllowAnonymous]
    [HttpPost("verify-email")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> VerifyEmail([FromBody] VerifyEmailRequest request, CancellationToken cancellationToken)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Token) || request.Token.Length > 128)
            return BadRequest(new { message = "O link de confirmação é inválido ou expirou." });

        await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var now = DateTime.UtcNow;
        var tokenHash = OneTimeTokens.Hash(request.Token.Trim());
        var user = await _context.Users
            .Include(candidate => candidate.Company)
            .SingleOrDefaultAsync(candidate =>
                candidate.RequiresEmailVerification &&
                candidate.EmailVerifiedAt == null &&
                candidate.EmailVerificationTokenHash == tokenHash &&
                candidate.EmailVerificationTokenExpiresAt > now,
                cancellationToken);

        if (user == null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return BadRequest(new { message = "O link de confirmação é inválido ou expirou." });
        }

        var claimed = await _context.Users
            .Where(candidate => candidate.Id == user.Id &&
                                candidate.EmailVerifiedAt == null &&
                                candidate.EmailVerificationTokenHash == tokenHash &&
                                candidate.EmailVerificationTokenExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.EmailVerifiedAt, now)
                .SetProperty(candidate => candidate.EmailVerificationTokenHash, (string?)null)
                .SetProperty(candidate => candidate.EmailVerificationTokenExpiresAt, (DateTime?)null), cancellationToken);
        if (claimed != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return BadRequest(new { message = "O link de confirmação é inválido ou expirou." });
        }

        await _context.Entry(user).ReloadAsync(cancellationToken);
        var accountActivated = !user.IsActive && user.Company?.ActivationStatus == CompanyActivationStatuses.Active;
        if (accountActivated)
        {
            user.IsActive = true;
            user.CredentialVersion++;
            await _context.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        await RecordAuditAsync(
            "auth.email_verified",
            "User",
            user.Id.ToString(),
            user.CompanyId,
            new { accountActivated });

        var message = user.Company?.ActivationStatus == CompanyActivationStatuses.Pending
            ? "E-mail confirmado. A empresa ainda aguarda análise da plataforma."
            : user.IsActive
                ? "E-mail confirmado. Você já pode entrar na plataforma."
                : "E-mail confirmado. A conta ainda não está liberada para acesso.";
        return Ok(new { emailVerified = true, accountActive = user.IsActive, message });
    }

    [AllowAnonymous]
    [HttpPost("resend-email-verification")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ResendEmailVerification(
        [FromBody] ResendEmailVerificationRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = RegistrationIdentity.NormalizeEmail(request?.Email);
        if (string.IsNullOrWhiteSpace(normalizedEmail) || normalizedEmail.Length > 320 ||
            !MailAddress.TryCreate(normalizedEmail, out var parsedEmail) ||
            !string.Equals(parsedEmail!.Address, normalizedEmail, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Informe um endereço de e-mail válido." });

        var user = await _context.Users.SingleOrDefaultAsync(candidate =>
            candidate.NormalizedEmail == normalizedEmail &&
            candidate.RequiresEmailVerification &&
            candidate.EmailVerifiedAt == null &&
            candidate.Company != null &&
            candidate.Company.ActivationStatus != CompanyActivationStatuses.Rejected,
            cancellationToken);

        if (user != null)
        {
            var rawToken = OneTimeTokens.Create();
            var expiresAt = DateTime.UtcNow.AddHours(24);
            user.EmailVerificationTokenHash = OneTimeTokens.Hash(rawToken);
            user.EmailVerificationTokenExpiresAt = expiresAt;
            await _context.SaveChangesAsync(cancellationToken);

            var emailSent = _accountEmailSender != null && await _accountEmailSender.SendEmailVerificationAsync(
                user.Email,
                rawToken,
                expiresAt,
                cancellationToken);
            await RecordAuditAsync(
                "auth.email_verification_resent",
                "User",
                user.Id.ToString(),
                user.CompanyId,
                new { emailSent });
        }

        return Accepted(new
        {
            message = "Se houver um cadastro aguardando confirmação, enviaremos um novo link para o endereço informado."
        });
    }

    [AllowAnonymous]
    [HttpPost("accept-invitation")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> AcceptInvitation(
        [FromBody] AcceptInvitationRequest request,
        CancellationToken cancellationToken)
    {
        if (request == null ||
            string.IsNullOrWhiteSpace(request.InvitationToken) ||
            string.IsNullOrWhiteSpace(request.Name) ||
            string.IsNullOrWhiteSpace(request.Cpf) ||
            string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { message = "Preencha nome, CPF e senha para aceitar o convite." });
        }

        if (request.Name.Trim().Length > 200)
            return BadRequest(new { message = "O nome deve ter até 200 caracteres." });
        if (request.Password.Length < 12 || request.Password.Length > 256)
            return BadRequest(new { message = "A senha deve ter pelo menos 12 caracteres." });
        if (request.InvitationToken.Length > 128)
            return BadRequest(new { message = "Este convite é inválido, expirou ou já foi utilizado." });
        if (!request.AcceptedPrivacyPolicy)
            return BadRequest(new { message = "Você precisa aceitar a Política de Privacidade." });

        var cpf = RegistrationIdentity.NormalizeCpf(request.Cpf);
        if (!RegistrationIdentity.IsValidCpf(cpf))
            return BadRequest(new { message = "Informe um CPF válido com 11 dígitos." });

        await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var now = DateTime.UtcNow;
        var tokenHash = OneTimeTokens.Hash(request.InvitationToken.Trim());
        var invitation = await _context.UserInvitations
            .AsNoTracking()
            .Include(item => item.Company)
            .SingleOrDefaultAsync(item => item.TokenHash == tokenHash, cancellationToken);

        if (invitation == null || invitation.AcceptedAt.HasValue || invitation.ExpiresAt <= now ||
            invitation.Company == null || invitation.Company.ActivationStatus != CompanyActivationStatuses.Active)
        {
            await transaction.RollbackAsync(cancellationToken);
            return BadRequest(new { message = "Este convite é inválido, expirou ou já foi utilizado." });
        }

        var normalizedInvitationEmail = RegistrationIdentity.NormalizeEmail(invitation.Email);
        if (await _context.Users.AnyAsync(user => user.NormalizedEmail == normalizedInvitationEmail, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict(new { message = "Já existe uma conta cadastrada com este e-mail." });
        }

        if (await _context.Users.AnyAsync(user => user.NormalizedCpf == cpf, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict(new { message = "Este CPF já está cadastrado no sistema." });
        }

        var claimed = await _context.UserInvitations
            .Where(item => item.Id == invitation.Id && item.AcceptedAt == null && item.ExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.AcceptedAt, now), cancellationToken);
        if (claimed != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict(new { message = "Este convite já foi utilizado ou expirou." });
        }

        var user = new User
        {
            Name = request.Name.Trim(),
            Email = invitation.Email,
            NormalizedEmail = normalizedInvitationEmail,
            Cpf = cpf,
            NormalizedCpf = cpf,
            Role = invitation.Role,
            CompanyId = invitation.CompanyId,
            Company = invitation.Company,
            CreatedAt = now,
            AcceptedPrivacyPolicy = true,
            PrivacyPolicyAcceptedAt = now
        };
        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, request.Password);
        _context.Users.Add(user);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict(new { message = "Este e-mail ou CPF já está cadastrado no sistema." });
        }

        var session = await IssueSessionAsync(user);
        await transaction.CommitAsync(cancellationToken);
        await RecordAuditAsync("user.invitation_accepted", "User", user.Id.ToString(), user.CompanyId);
        _cookieService.Write(Response, session.Refresh.Token, session.Refresh.ExpiresAt);
        return Ok(session.Response);
    }

    [AllowAnonymous]
    [HttpPost("reset-password")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ResetPassword(
        [FromBody] ResetPasswordRequest request,
        CancellationToken cancellationToken)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.ResetToken) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new { message = "Informe o link de recuperação e a nova senha." });
        if (request.ResetToken.Length > 128)
            return BadRequest(new { message = "Este link de recuperação é inválido ou expirou." });
        if (request.Password.Length < 12 || request.Password.Length > 256)
            return BadRequest(new { message = "A senha deve ter entre 12 e 256 caracteres." });

        await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var now = DateTime.UtcNow;
        var tokenHash = OneTimeTokens.Hash(request.ResetToken.Trim());
        var reset = await _context.PasswordResetTokens
            .AsNoTracking()
            .Include(token => token.User)
            .SingleOrDefaultAsync(token => token.TokenHash == tokenHash, cancellationToken);

        if (reset == null || reset.UsedAt.HasValue || reset.RevokedAt.HasValue || reset.ExpiresAt <= now || reset.User is not { IsActive: true })
        {
            await transaction.RollbackAsync(cancellationToken);
            return BadRequest(new { message = "Este link de recuperação é inválido ou expirou." });
        }

        var claimed = await _context.PasswordResetTokens
            .Where(token => token.Id == reset.Id && token.UsedAt == null && token.RevokedAt == null && token.ExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.UsedAt, now), cancellationToken);
        if (claimed != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return BadRequest(new { message = "Este link de recuperação é inválido ou expirou." });
        }

        var user = await _context.Users.SingleOrDefaultAsync(
            candidate => candidate.Id == reset.UserId && candidate.IsActive,
            cancellationToken);
        if (user == null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return BadRequest(new { message = "Este link de recuperação é inválido ou expirou." });
        }

        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, request.Password);
        user.CredentialVersion++;
        await _context.SaveChangesAsync(cancellationToken);
        await _context.RefreshSessions
            .Where(session => session.UserId == user.Id && session.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(session => session.RevokedAt, now), cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        if (_panelHub != null)
            await _panelHub.DisconnectUserAsync(user.Id);
        await RecordAuditAsync("auth.password_reset_completed", "User", user.Id.ToString(), user.CompanyId);
        return NoContent();
    }

    [AllowAnonymous]
    [HttpPost("refresh")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Refresh(CancellationToken cancellationToken)
    {
        var refreshToken = _cookieService.Read(Request);
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return Unauthorized(new
            {
                code = "session_expired",
                message = "Sua sessão expirou. Entre novamente."
            });
        }

        var result = await _refreshSessions.RotateAsync(
            refreshToken,
            GetRemoteIpAddress(),
            Request.Headers.UserAgent.ToString(),
            cancellationToken);

        if (result.Status == RefreshRotationStatus.Concurrent)
        {
            return StatusCode(StatusCodes.Status409Conflict, new
            {
                code = "refresh_in_progress",
                message = "A sessão já está sendo renovada. Tente novamente."
            });
        }

        if (result.Status != RefreshRotationStatus.Succeeded ||
            result.User == null ||
            (result.User.Company != null && result.User.Company.ActivationStatus != CompanyActivationStatuses.Active) ||
            result.Token == null ||
            !result.ExpiresAt.HasValue)
        {
            _cookieService.Delete(Response);
            return Unauthorized(new
            {
                code = "session_expired",
                message = "Sua sessão expirou. Entre novamente."
            });
        }

        var accessToken = _tokenService.CreateAccessToken(result.User);
        _cookieService.Write(Response, result.Token, result.ExpiresAt.Value);
        return Ok(CreateAuthResponse(result.User, accessToken));
    }

    [AllowAnonymous]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        var refreshToken = _cookieService.Read(Request);
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            await _refreshSessions.RevokeAsync(refreshToken, cancellationToken);
        }

        await RecordAuditAsync("auth.logout", "Auth");
        _cookieService.Delete(Response);
        return NoContent();
    }

    [Authorize]
    [HttpGet("me")]
    public IActionResult GetCurrentUser()
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var email = User.FindFirst(ClaimTypes.Email)?.Value;
        var name = User.FindFirst(ClaimTypes.Name)?.Value;
        var role = User.FindFirst(ClaimTypes.Role)?.Value;
        var companyId = User.FindFirst("company_id")?.Value;
        var companyName = User.FindFirst("company_name")?.Value;

        if (userId == null)
        {
            return Unauthorized(new { message = "Usuário não identificado." });
        }

        return Ok(new
        {
            id = userId,
            name,
            email,
            role,
            companyId,
            companyName
        });
    }

    private async Task<AuthResponse> CreateSessionAsync(User user)
    {
        var session = await IssueSessionAsync(user);
        _cookieService.Write(Response, session.Refresh.Token, session.Refresh.ExpiresAt);
        return session.Response;
    }

    private async Task<(AuthResponse Response, IssuedRefreshSession Refresh)> IssueSessionAsync(User user)
    {
        var accessToken = _tokenService.CreateAccessToken(user);
        var refreshSession = await _refreshSessions.CreateAsync(
            user,
            GetRemoteIpAddress(),
            Request.Headers.UserAgent.ToString(),
            HttpContext.RequestAborted);
        return (CreateAuthResponse(user, accessToken), refreshSession);
    }

    private static AuthResponse CreateAuthResponse(User user, AccessTokenResult accessToken)
    {
        return new AuthResponse(
            accessToken.Token,
            accessToken.ExpiresAt,
            new AuthUserResponse(
                user.Id,
                user.Name,
                user.Email,
                user.Role,
                user.CompanyId,
                user.Company?.Name));
    }

    private string? GetRemoteIpAddress() => HttpContext.Connection.RemoteIpAddress?.ToString();

    private static bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.InnerException switch
        {
            PostgresException postgresException => postgresException.SqlState == PostgresErrorCodes.UniqueViolation,
            SqliteException sqliteException => sqliteException.SqliteExtendedErrorCode is 1555 or 2067,
            _ => false
        };

    private Task RecordAuditAsync(
        string action,
        string resourceType,
        string? resourceId = null,
        Guid? companyId = null,
        object? metadata = null) =>
        _audit?.RecordAsync(action, resourceType, resourceId, companyId, metadata, HttpContext.RequestAborted)
        ?? Task.CompletedTask;

}

public sealed record AuthResponse(string Token, DateTime ExpiresAt, AuthUserResponse User);

public sealed record AuthUserResponse(
    Guid Id,
    string Name,
    string Email,
    string Role,
    Guid? CompanyId,
    string? CompanyName);

public class LoginRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class RegisterRequest
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Cpf { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string CompanyCnpj { get; set; } = string.Empty;
    public bool AcceptedPrivacyPolicy { get; set; }
}

public sealed class VerifyEmailRequest
{
    public string Token { get; set; } = string.Empty;
}

public sealed class ResendEmailVerificationRequest
{
    public string Email { get; set; } = string.Empty;
}

public sealed class AcceptInvitationRequest
{
    public string InvitationToken { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Cpf { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool AcceptedPrivacyPolicy { get; set; }
}

public sealed class ResetPasswordRequest
{
    public string ResetToken { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
