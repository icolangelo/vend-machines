namespace VendingMachines.Api.Models;

public sealed class DeviceMessageInbox
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string IdempotencyKey { get; set; } = string.Empty;
    public string MachineId { get; set; } = string.Empty;
    public Guid CompanyId { get; set; }
    public string Serial { get; set; } = string.Empty;
    public string ConnectionId { get; set; } = string.Empty;
    public int MessageId { get; set; }
    public string DataJson { get; set; } = "{}";
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "Pending";
    public int Attempts { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public DateTime? ProcessingStartedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public string? LastError { get; set; }
}
