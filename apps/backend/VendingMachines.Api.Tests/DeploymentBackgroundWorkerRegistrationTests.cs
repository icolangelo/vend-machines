using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class DeploymentBackgroundWorkerRegistrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PassiveModeRegistersNoHostedWorkers(bool includePostgresTelemetryPanelRelay)
    {
        var services = new ServiceCollection();

        services.AddDeploymentBackgroundWorkers(
            passiveDeploymentMode: true,
            includePostgresTelemetryPanelRelay);

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void ActivePostgresModeRegistersEveryBackgroundWorker()
    {
        var services = new ServiceCollection();

        services.AddDeploymentBackgroundWorkers(
            passiveDeploymentMode: false,
            includePostgresTelemetryPanelRelay: true);

        Assert.Equal(9, services.Count(descriptor => descriptor.ServiceType == typeof(IHostedService)));
    }

    [Fact]
    public void ActiveSqliteModeOmitsOnlyThePostgresTelemetryRelay()
    {
        var services = new ServiceCollection();

        services.AddDeploymentBackgroundWorkers(
            passiveDeploymentMode: false,
            includePostgresTelemetryPanelRelay: false);

        Assert.Equal(8, services.Count(descriptor => descriptor.ServiceType == typeof(IHostedService)));
    }
}
