using System;
using System.Collections.Generic;

namespace VendingMachines.Api.Models;

public class Location
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Company? Company { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public ICollection<Machine> Machines { get; set; } = new List<Machine>();
}
