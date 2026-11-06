namespace VendingMachines.Api.Services;

/// <summary>
/// Operations that application services require from the MDB connection owner.
/// Keeping this contract separate from the WebSocket host allows a future
/// routed implementation without coupling domain services to the process-local manager.
/// </summary>
public interface ITelemetryConnectionOperations : ITelemetryCommandSender
{
    string NodeId { get; }

    IDisposable? TryBeginSessionStart();

    bool IsLocalConnectionOnline(string serial, string? expectedConnectionId);

    Task<DeviceCommandResult> DisconnectMachineAsync(
        string serial,
        string reason,
        CancellationToken cancellationToken = default);

    Task<bool> IsOnlineAsync(string serial, CancellationToken cancellationToken = default);

    Task<bool> HasCommandRouteAsync(string serial, CancellationToken cancellationToken = default);

    Task<HashSet<string>> GetOnlineSerialsAsync(
        IEnumerable<string> serials,
        CancellationToken cancellationToken = default);

    Task<DeviceStatusResult> SendStatusRequestAsync(
        string serial,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
