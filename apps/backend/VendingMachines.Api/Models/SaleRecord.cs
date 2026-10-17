namespace VendingMachines.Api.Models;

public sealed class SaleRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public string MachineId { get; set; } = string.Empty;
    public Guid SessionId { get; set; }
    public Guid TransactionId { get; set; }
    public int ItemNumber { get; set; }
    public string? ProductId { get; set; }
    public string? ProductNameSnapshot { get; set; }
    public int Quantity { get; set; } = 1;
    public decimal GrossAmount { get; set; }
    public decimal ApplicationFee { get; set; }
    public decimal NetAmount { get; set; }
    public decimal RefundedAmount { get; set; }
    public string FinancialStatus { get; set; } = "Settled";
    public string Origin { get; set; } = "DeliveryConfirmed";
    public DateTime SoldAt { get; set; } = DateTime.UtcNow;
}
