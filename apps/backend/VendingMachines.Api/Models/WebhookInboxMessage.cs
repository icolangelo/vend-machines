namespace VendingMachines.Api.Models;

public sealed class WebhookInboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string EventKey { get; set; } = string.Empty;
    public string PaymentId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string EventAction { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public string AuthenticationMode { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "Pending";
    public int Attempts { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public DateTime? ProcessingStartedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public string? LastError { get; set; }
}
