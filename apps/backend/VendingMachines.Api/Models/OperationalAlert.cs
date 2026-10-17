namespace VendingMachines.Api.Models;

public sealed class OperationalAlert
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public string? MachineId { get; set; }
    public Guid? SessionId { get; set; }
    public Guid? TransactionId { get; set; }
    public string DeduplicationKey { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Severity { get; set; } = "warning";
    public string Status { get; set; } = "Active";
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
    public string? Resolution { get; set; }
}

public sealed class AlertReadReceipt
{
    public Guid AlertId { get; set; }
    public Guid UserId { get; set; }
    public DateTime ReadAt { get; set; } = DateTime.UtcNow;
}
