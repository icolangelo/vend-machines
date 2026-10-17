using System;
using VendingMachines.Api.Services;

namespace VendingMachines.Api.Models;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string NormalizedEmail { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = AppRoles.Admin;
    public bool IsActive { get; set; } = true;
    public int CredentialVersion { get; set; }
    public Guid? CompanyId { get; set; }
    public Company? Company { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string Cpf { get; set; } = string.Empty;
    public string NormalizedCpf { get; set; } = string.Empty;
    public bool AcceptedPrivacyPolicy { get; set; } = false;
    public DateTime? PrivacyPolicyAcceptedAt { get; set; }
    public bool RequiresEmailVerification { get; set; }
    public DateTime? EmailVerifiedAt { get; set; }
    public string? EmailVerificationTokenHash { get; set; }
    public DateTime? EmailVerificationTokenExpiresAt { get; set; }
}
