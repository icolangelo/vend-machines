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

    // Coleção de usuários (sócios) vinculados a esta empresa
    public ICollection<User> Users { get; set; } = new List<User>();

    // Coleção de máquinas vinculadas a esta empresa
    public ICollection<Machine> Machines { get; set; } = new List<Machine>();
}
