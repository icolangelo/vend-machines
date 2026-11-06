using System;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;

namespace VendingMachines.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly AuthTokenService _tokenService;
    private readonly RefreshSessionService _refreshSessions;
    private readonly AuthCookieService _cookieService;

    public AuthController(
        AppDbContext context,
        AuthTokenService tokenService,
        RefreshSessionService refreshSessions,
        AuthCookieService cookieService)
    {
        _context = context;
        _tokenService = tokenService;
        _refreshSessions = refreshSessions;
        _cookieService = cookieService;
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { message = "E-mail e senha são obrigatórios." });
        }

        var user = await _context.Users.Include(u => u.Company).FirstOrDefaultAsync(u => u.Email.ToLower() == request.Email.ToLower());
        if (user == null)
        {
            return Unauthorized(new { message = "Credenciais inválidas. E-mail ou senha incorretos." });
        }

        var hasher = new PasswordHasher<User>();
        var result = hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);

        if (result == PasswordVerificationResult.Failed)
        {
            return Unauthorized(new { message = "Credenciais inválidas. E-mail ou senha incorretos." });
        }

        return Ok(await CreateSessionAsync(user));
    }

    [HttpPost("register")]
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

        if (!request.AcceptedPrivacyPolicy)
        {
            return BadRequest(new { message = "Você precisa aceitar a Política de Privacidade para se cadastrar." });
        }

        // Validar e-mail único
        if (await _context.Users.AnyAsync(u => u.Email.ToLower() == request.Email.ToLower()))
        {
            return BadRequest(new { message = "Este e-mail já está sendo utilizado por outro usuário." });
        }

        // Validar CPF único
        if (await _context.Users.AnyAsync(u => u.Cpf == request.Cpf))
        {
            return BadRequest(new { message = "Este CPF já está cadastrado no sistema." });
        }

        // Validar CNPJ único
        if (await _context.Companies.AnyAsync(c => c.Cnpj == request.CompanyCnpj))
        {
            return BadRequest(new { message = "Este CNPJ já está cadastrado no sistema." });
        }

        using var transaction = await _context.Database.BeginTransactionAsync();
        try
        {
            // Criar a empresa primeiro
            var company = new Company
            {
                Name = request.CompanyName,
                Cnpj = request.CompanyCnpj,
                CreatedAt = DateTime.UtcNow
            };
            _context.Companies.Add(company);
            await _context.SaveChangesAsync();

            _context.Locations.AddRange(CreateDefaultLocations(company));
            await _context.SaveChangesAsync();

            // Criar o usuário
            var user = new User
            {
                Name = request.Name,
                Email = request.Email,
                Cpf = request.Cpf,
                Role = "Admin", // O criador original da empresa é o Administrador
                CompanyId = company.Id,
                CreatedAt = DateTime.UtcNow,
                AcceptedPrivacyPolicy = true,
                PrivacyPolicyAcceptedAt = DateTime.UtcNow
            };

            var hasher = new PasswordHasher<User>();
            user.PasswordHash = hasher.HashPassword(user, request.Password);

            _context.Users.Add(user);
            await _context.SaveChangesAsync();

            // Definir o ID do criador da empresa
            company.CreatedByUserId = user.Id;
            await _context.SaveChangesAsync();

            user.Company = company;
            var session = await IssueSessionAsync(user);
            await transaction.CommitAsync();
            _cookieService.Write(Response, session.Refresh.Token, session.Refresh.ExpiresAt);
            return Ok(session.Response);
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
            return StatusCode(500, new { message = "Erro ao processar o cadastro: " + ex.Message });
        }
    }

    [AllowAnonymous]
    [HttpPost("refresh")]
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

    private static IEnumerable<Location> CreateDefaultLocations(Company company)
    {
        var prefix = DatabaseDataService.GetCompanyPrefix(company.Name);
        return new[]
        {
            new Location { CompanyId = company.Id, Name = $"{prefix} - Shopping", CreatedAt = DateTime.UtcNow },
            new Location { CompanyId = company.Id, Name = $"{prefix} - Hospital", CreatedAt = DateTime.UtcNow }
        };
    }
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
