namespace VendingMachines.Api.Models;

public sealed class InventoryMovement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public string MachineId { get; set; } = string.Empty;
    public int ItemNumber { get; set; }
    public string ProductId { get; set; } = string.Empty;
    public Guid? SaleRecordId { get; set; }
    public Guid? UserId { get; set; }
    public string MovementType { get; set; } = string.Empty;
    public int QuantityDelta { get; set; }
    public int QuantityAfter { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
