using System;
using System.Collections.Generic;

namespace VendingMachines.Api.Models;

public class Company
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string Cnpj { get; set; } = string.Empty;
    public string NormalizedCnpj { get; set; } = string.Empty;
    public string ActivationStatus { get; set; } = CompanyActivationStatuses.Pending;

    // Coleção de usuários (sócios) vinculados a esta empresa
    public ICollection<User> Users { get; set; } = new List<User>();

    // Coleção de máquinas vinculadas a esta empresa
    public ICollection<Machine> Machines { get; set; } = new List<Machine>();

    // Coleção de localizações vinculadas a esta empresa
    public ICollection<Location> Locations { get; set; } = new List<Location>();
}

public static class CompanyActivationStatuses
{
    public const string Pending = "Pending";
    public const string Active = "Active";
    public const string Rejected = "Rejected";
}
