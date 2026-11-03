namespace VendingMachines.Api.Models;

/// <summary>
/// Shared operational switch used to stop admitting new vend sessions during a controlled release.
/// Existing telemetry, session completion, and payment processing continue while admission is paused.
/// </summary>
public sealed class SessionAdmissionGate
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public bool NewSessionsPaused { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string UpdatedBy { get; set; } = "migration";
    public string Reason { get; set; } = "default";
}
