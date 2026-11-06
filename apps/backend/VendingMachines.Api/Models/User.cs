using System;

namespace VendingMachines.Api.Models;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = "Admin";
    public Guid? CompanyId { get; set; }
    public Company? Company { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string Cpf { get; set; } = string.Empty;
    public bool AcceptedPrivacyPolicy { get; set; } = false;
    public DateTime? PrivacyPolicyAcceptedAt { get; set; }
}
