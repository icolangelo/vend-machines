using System;

namespace VendingMachines.Api.Models;

public class LocationDto
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int MachineCount { get; set; }
    public decimal Revenue30d { get; set; }
    public int TotalSales30d { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
