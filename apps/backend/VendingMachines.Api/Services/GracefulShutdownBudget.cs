namespace VendingMachines.Api.Services;

public static class GracefulShutdownBudget
{
    public const int HostTimeoutSeconds = 45;
    public const int TelemetryDiagnosticTimeoutSeconds = 2;
    public const int TelemetryWorkerStopTimeoutSeconds = 2;
    public const int HostSafetyMarginSeconds = 1;
    public const int MaximumSessionAdmissionWaitSeconds = 20;
    public const int MaximumTelemetryDrainTimeoutSeconds =
        HostTimeoutSeconds
        - TelemetryDiagnosticTimeoutSeconds
        - TelemetryWorkerStopTimeoutSeconds
        - HostSafetyMarginSeconds;
    public const int DefaultTelemetryDrainTimeoutSeconds = MaximumTelemetryDrainTimeoutSeconds;

    public static TimeSpan GetTelemetryDrainTimeout(int seconds)
    {
        if (seconds is < 1 or > MaximumTelemetryDrainTimeoutSeconds)
            throw new InvalidOperationException(
                $"Telemetry:ApplicationStoppingDrainTimeoutSeconds deve ficar entre 1 e {MaximumTelemetryDrainTimeoutSeconds} segundos para preservar a margem de encerramento do host.");

        return TimeSpan.FromSeconds(seconds);
    }

    public static TimeSpan GetSessionAdmissionTimeout(TimeSpan telemetryDrainTimeout) =>
        TimeSpan.FromTicks(Math.Min(
            TimeSpan.FromSeconds(MaximumSessionAdmissionWaitSeconds).Ticks,
            telemetryDrainTimeout.Ticks / 2));
}
