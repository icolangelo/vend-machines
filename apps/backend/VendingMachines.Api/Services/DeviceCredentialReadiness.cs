using VendingMachines.Api.Models;

namespace VendingMachines.Api.Services;

public static class DeviceCredentialReadiness
{
    public static bool HasActiveMachinesWithoutValidCredential(IQueryable<Machine> machines) =>
        machines.Any(machine => machine.IsActive &&
            (string.IsNullOrWhiteSpace(machine.TelemetryCredentialHash) ||
             machine.TelemetryCredentialRevokedAt.HasValue));
}
