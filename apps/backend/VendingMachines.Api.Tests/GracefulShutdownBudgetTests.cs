using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class GracefulShutdownBudgetTests
{
    [Fact]
    public void TelemetryDrainAndWorkerBudgetsLeaveTheConfiguredHostMargin()
    {
        Assert.InRange(
            GracefulShutdownBudget.DefaultTelemetryDrainTimeoutSeconds,
            1,
            GracefulShutdownBudget.MaximumTelemetryDrainTimeoutSeconds);
        Assert.Equal(
            GracefulShutdownBudget.HostSafetyMarginSeconds,
            GracefulShutdownBudget.HostTimeoutSeconds
                - GracefulShutdownBudget.MaximumTelemetryDrainTimeoutSeconds
                - GracefulShutdownBudget.TelemetryDiagnosticTimeoutSeconds
                - GracefulShutdownBudget.TelemetryWorkerStopTimeoutSeconds);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(40)]
    public void AcceptsDrainTimeoutsInsideTheHostBudget(int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), GracefulShutdownBudget.GetTelemetryDrainTimeout(seconds));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(41)]
    public void RejectsDrainTimeoutsThatCouldOverrunTheHostBudget(int seconds)
    {
        Assert.Throws<InvalidOperationException>(() => GracefulShutdownBudget.GetTelemetryDrainTimeout(seconds));
    }

    [Theory]
    [InlineData(1, 0.5)]
    [InlineData(10, 5)]
    [InlineData(40, 20)]
    public void SessionAdmissionWaitReservesHalfOfConfiguredDrainBudget(int configuredSeconds, double expectedSeconds)
    {
        var drainTimeout = GracefulShutdownBudget.GetTelemetryDrainTimeout(configuredSeconds);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), GracefulShutdownBudget.GetSessionAdmissionTimeout(drainTimeout));
    }
}
