using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;

namespace VendingMachines.Api.Controllers;

[Authorize(Roles = "Admin")]
[ApiController]
[Route("api/[controller]")]
public class CompaniesController : ControllerBase
{
    private readonly AppDbContext _context;

    public CompaniesController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<IActionResult> GetPaginatedCompanies(
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? searchTerm = null)
    {
        var email = User.FindFirst(ClaimTypes.Email)?.Value;
        if (email != "dev.ivan@gmail.com")
        {
            return StatusCode(403, new { message = "Apenas o administrador do sistema pode listar as empresas." });
        }

        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1) pageSize = 10;
        if (pageSize > 100) pageSize = 100;

        var query = _context.Companies.AsQueryable();

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var search = searchTerm.ToLower();
            query = query.Where(c => c.Name.ToLower().Contains(search));
        }

        var totalItems = await query.CountAsync();
        var totalPages = (int)Math.Ceiling((double)totalItems / pageSize);

        var items = await query
            .OrderBy(c => c.Name)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new CompanyDto
            {
                Id = c.Id,
                Name = c.Name,
                Cnpj = c.Cnpj,
                CreatedAt = c.CreatedAt,
                // Nome do criador/dono original
                CreatedBy = _context.Users
                    .Where(u => u.Id == c.CreatedByUserId)
                    .Select(u => u.Name)
                    .FirstOrDefault() ?? "Desconhecido",
                // Lista de sócios associados a esta mesma empresa (exclui o criador)
                Partners = _context.Users
                    .Where(u => u.CompanyId == c.Id && u.Id != c.CreatedByUserId)
                    .Select(u => u.Name)
                    .ToList(),
                // Lista de integrações habilitadas
                EnabledIntegrations = _context.MercadoPagoIntegrations
                    .Where(i => i.CompanyId == c.Id && i.IsActive)
                    .Select(i => "Mercado Pago")
                    .ToList()
            })
            .ToListAsync();

        return Ok(new PaginatedResult<CompanyDto>
        {
            Items = items,
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalItems = totalItems,
            TotalPages = totalPages
        });
    }

    [HttpGet("{companyId}/integration")]
    public async Task<IActionResult> GetCompanyIntegration(Guid companyId)
    {
        var email = User.FindFirst(ClaimTypes.Email)?.Value;
        if (email != "dev.ivan@gmail.com")
        {
            return StatusCode(403, new { message = "Apenas o administrador do sistema pode visualizar os detalhes da integração." });
        }

        var integration = await _context.MercadoPagoIntegrations
            .FirstOrDefaultAsync(i => i.CompanyId == companyId);

        if (integration == null)
        {
            return NotFound(new { message = "Esta empresa não possui integração configurada." });
        }

        // Decifra para obter os valores originais antes de mascará-los para envio
        var decryptedAccessToken = EncryptionService.Decrypt(integration.AccessToken);
        var decryptedClientSecret = EncryptionService.Decrypt(integration.ClientSecret);

        var dto = new MercadoPagoIntegrationDto
        {
            Id = integration.Id,
            CompanyId = integration.CompanyId,
            OwnerName = integration.OwnerName,
            OwnerCpf = integration.OwnerCpf,
            OwnerEmail = integration.OwnerEmail,
            OwnerPhone = integration.OwnerPhone,
            BusinessName = integration.BusinessName,
            TradeName = integration.TradeName,
            Cnpj = integration.Cnpj,
            BusinessEmail = integration.BusinessEmail,
            BusinessPhone = integration.BusinessPhone,
            PublicKey = integration.PublicKey,
            ClientId = integration.ClientId,
            HasRefreshToken = !string.IsNullOrWhiteSpace(integration.RefreshToken),
            AccessTokenExpiresAt = integration.AccessTokenExpiresAt,
            MercadoPagoUserId = integration.MercadoPagoUserId,
            MercadoPagoNickname = integration.MercadoPagoNickname,
            MercadoPagoSiteId = integration.MercadoPagoSiteId,
            LastTokenValidationAt = integration.LastTokenValidationAt,
            LastTokenValidationStatus = integration.LastTokenValidationStatus,
            TokenFingerprint = GetTokenFingerprint(decryptedAccessToken),
            IsActive = integration.IsActive,
            CreatedAt = integration.CreatedAt,
            UpdatedAt = integration.UpdatedAt,
            AccessToken = MaskValue(decryptedAccessToken),
            ClientSecret = MaskValue(decryptedClientSecret)
        };

        return Ok(dto);
    }

    private static string MaskValue(string value, int visibleStart = 8, int visibleEnd = 4)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.Length <= visibleStart + visibleEnd)
        {
            return new string('•', value.Length);
        }
        return value[..visibleStart] + new string('•', 12) + value[^visibleEnd..];
    }

    private static string GetTokenFingerprint(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return "empty";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash)[..12];
    }
}

public class CompanyDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Cnpj { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public List<string> Partners { get; set; } = new();
    public List<string> EnabledIntegrations { get; set; } = new();
    public DateTime CreatedAt { get; set; }
}
