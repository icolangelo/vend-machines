namespace VendingMachines.Api.Services;

public interface ITelemetryCommandSender
{
    Task<DeviceCommandResult> SendCommandAsync(
        string serial,
        object data,
        CancellationToken cancellationToken = default);
}
