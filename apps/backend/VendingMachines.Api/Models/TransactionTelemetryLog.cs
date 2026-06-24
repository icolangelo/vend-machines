using System;
using System.Text.Json.Serialization;

namespace VendingMachines.Api.Models;

public class TransactionTelemetryLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TransactionId { get; set; }
    
    [JsonIgnore]
    public PaymentTransaction? Transaction { get; set; }
    
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string LogType { get; set; } = "Info"; // Info, CommandSent, AckReceived, Error, RefundTriggered
    public string Message { get; set; } = string.Empty;
}
