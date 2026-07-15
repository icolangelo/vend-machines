using System.Text.Json.Serialization;

namespace VendingMachines.Api.Models;

public static class MachineSessionStates
{
    public const string Opening = "Opening";
    public const string AwaitingSelection = "AwaitingSelection";
    public const string ClosingWithoutSelection = "ClosingWithoutSelection";
    public const string DeviceClosingBeforeSelection = "DeviceClosingBeforeSelection";
    public const string PaymentPending = "PaymentPending";
    public const string PaymentApproved = "PaymentApproved";
    public const string AwaitingDeliveryResult = "AwaitingDeliveryResult";
    public const string DeliveryFailed = "DeliveryFailed";
    public const string RefundPending = "RefundPending";
    public const string Refunded = "Refunded";
    public const string ReconciliationRequired = "ReconciliationRequired";
    public const string DeniedAwaitingClosure = "DeniedAwaitingClosure";
    public const string Completed = "Completed";
    public const string ClosedWithoutSelection = "ClosedWithoutSelection";
    public const string ClosedByDeviceBeforeSelection = "ClosedByDeviceBeforeSelection";
    public const string ClosureIncomplete = "ClosureIncomplete";
    public const string Denied = "Denied";

    public static bool IsTerminal(string state) => state is
        Completed or ClosedWithoutSelection or ClosedByDeviceBeforeSelection or
        ClosureIncomplete or Denied or Refunded or ReconciliationRequired;
}

public class MachineConnectionState
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string MachineId { get; set; } = string.Empty;
    public Guid CompanyId { get; set; }
    public bool MonitoringEnabled { get; set; }
    public bool IsOnline { get; set; }
    public string? ConnectionId { get; set; }
    public DateTime? ConnectedAt { get; set; }
    public DateTime? DisconnectedAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime? ActivatedAt { get; set; }
    public Guid? ActivatedByUserId { get; set; }
    public DateTime? DeactivatedAt { get; set; }
    public Guid? DeactivatedByUserId { get; set; }

    [JsonIgnore]
    public Machine? Machine { get; set; }
}

public class MachineSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string MachineId { get; set; } = string.Empty;
    public Guid CompanyId { get; set; }
    public Guid? StartedByUserId { get; set; }
    public string Source { get; set; } = "panel";
    public string State { get; set; } = MachineSessionStates.Opening;
    public string? CloseReason { get; set; }
    public int? ItemNumber { get; set; }
    public int? AmountCents { get; set; }
    public Guid? TransactionId { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastEventAt { get; set; } = DateTime.UtcNow;
    public DateTime? SelectionDeadlineAt { get; set; }
    public DateTime? PaymentDeadlineAt { get; set; }
    public DateTime? DeliveryDeadlineAt { get; set; }
    public DateTime? ClosedAt { get; set; }

    [JsonIgnore]
    public Machine? Machine { get; set; }
    [JsonIgnore]
    public PaymentTransaction? Transaction { get; set; }
}

public class TelemetryCommand
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public string MachineId { get; set; } = string.Empty;
    public Guid? SessionId { get; set; }
    public Guid? TransactionId { get; set; }
    public Guid? UserId { get; set; }
    public int MessageId { get; set; }
    public string Direction { get; set; } = "server_to_device";
    public string Command { get; set; } = string.Empty;
    public string? DataJson { get; set; }
    public string Status { get; set; } = "accepted";
    public int Attempts { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SentAt { get; set; }
    public DateTime? AckAt { get; set; }
    public string? Error { get; set; }
}

public class TelemetryEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public string MachineId { get; set; } = string.Empty;
    public Guid? SessionId { get; set; }
    public Guid? TransactionId { get; set; }
    public Guid? UserId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public string? DataJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class DeliveryFailure
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public string MachineId { get; set; } = string.Empty;
    public Guid SessionId { get; set; }
    public Guid TransactionId { get; set; }
    public string Reason { get; set; } = "unknown";
    public string OriginalReason { get; set; } = "unknown";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class OutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public string MachineId { get; set; } = string.Empty;
    public Guid? SessionId { get; set; }
    public Guid? TransactionId { get; set; }
    public string MessageType { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public string Status { get; set; } = "pending";
    public int Attempts { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? NextAttemptAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public string? LastError { get; set; }
}
