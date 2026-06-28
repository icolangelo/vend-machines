using System;

namespace VendingMachines.Api.Models;

public class MercadoPagoIntegrationDto
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }

    // PF Data (Owner/Partner)
    public string OwnerName { get; set; } = string.Empty;
    public string OwnerCpf { get; set; } = string.Empty;
    public string OwnerEmail { get; set; } = string.Empty;
    public string OwnerPhone { get; set; } = string.Empty;

    // PJ Data (Company)
    public string BusinessName { get; set; } = string.Empty;
    public string TradeName { get; set; } = string.Empty;
    public string Cnpj { get; set; } = string.Empty;
    public string BusinessEmail { get; set; } = string.Empty;
    public string BusinessPhone { get; set; } = string.Empty;

    // Mercado Pago Credentials (Masked)
    public string AccessToken { get; set; } = string.Empty;
    public bool HasRefreshToken { get; set; }
    public DateTime? AccessTokenExpiresAt { get; set; }
    public string PublicKey { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string MercadoPagoUserId { get; set; } = string.Empty;
    public string MercadoPagoNickname { get; set; } = string.Empty;
    public string MercadoPagoSiteId { get; set; } = string.Empty;
    public DateTime? LastTokenValidationAt { get; set; }
    public string LastTokenValidationStatus { get; set; } = string.Empty;
    public string TokenFingerprint { get; set; } = string.Empty;

    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
