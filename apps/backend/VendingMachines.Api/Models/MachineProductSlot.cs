namespace VendingMachines.Api.Models;

public sealed class MachineProductSlot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public string MachineId { get; set; } = string.Empty;
    public int ItemNumber { get; set; }
    public string ProductId { get; set; } = string.Empty;
    public int Capacity { get; set; }
    public int CurrentStock { get; set; }
    public int LowStockThreshold { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
