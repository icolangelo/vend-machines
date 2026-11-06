using System;

namespace VendingMachines.Api.Models;

public class SystemSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool IsSingleton { get; set; } = true;
    public decimal ApplicationFeePercent { get; set; } = 5.0m;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
