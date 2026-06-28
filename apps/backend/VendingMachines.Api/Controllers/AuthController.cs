using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;

namespace VendingMachines.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IConfiguration _configuration;

    public AuthController(AppDbContext context, IConfiguration configuration)
    {
        _context = context;
        _configuration = configuration;
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

        // Gerar token JWT
        var token = GenerateJwtToken(user);

        return Ok(new
        {
            token,
            user = new
            {
                id = user.Id,
                name = user.Name,
                email = user.Email,
                role = user.Role,
                companyId = user.CompanyId,
                companyName = user.Company?.Name
            }
        });
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

            await transaction.CommitAsync();

            // Gerar token JWT e realizar login automático
            user.Company = company;
            var token = GenerateJwtToken(user);

            return Ok(new
            {
                token,
                user = new
                {
                    id = user.Id,
                    name = user.Name,
                    email = user.Email,
                    role = user.Role,
                    companyId = user.CompanyId,
                    companyName = company.Name
                }
            });
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            return StatusCode(500, new { message = "Erro ao processar o cadastro: " + ex.Message });
        }
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

    private string GenerateJwtToken(User user)
    {
        var jwtKey = _configuration["Jwt:Key"] ?? "SuperSecretKeyForVendingMachinesManager2026!";
        var issuer = _configuration["Jwt:Issuer"] ?? "VendingMachinesApi";
        var audience = _configuration["Jwt:Audience"] ?? "VendingMachinesApp";

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claimsList = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Name),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        if (user.CompanyId.HasValue)
        {
            claimsList.Add(new Claim("company_id", user.CompanyId.Value.ToString()));
            if (user.Company != null)
            {
                claimsList.Add(new Claim("company_name", user.Company.Name));
            }
        }

        var claims = claimsList.ToArray();

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddDays(7), // Expira em 7 dias
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

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
