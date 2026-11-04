using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace VendingMachines.Api.Services;

/// <summary>
/// Keeps all background processing behind the deployment mode gate so a passive
/// candidate cannot claim work from shared production resources.
/// </summary>
public static class DeploymentBackgroundWorkerRegistration
{
    public static IServiceCollection AddDeploymentBackgroundWorkers(
        this IServiceCollection services,
        bool passiveDeploymentMode,
        bool includePostgresTelemetryPanelRelay)
    {
        if (passiveDeploymentMode)
            return services;

        services.AddHostedService(sp => sp.GetRequiredService<PasswordResetEmailQueue>());
        if (includePostgresTelemetryPanelRelay)
            services.AddHostedService<TelemetryPanelEventRelay>();

        // Register before the manager so shutdown drains sockets before removing the node lease.
        services.AddHostedService(sp => new TelemetryConnectionLeaseWorker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<TelemetryConnectionManager>().NodeId,
            sp.GetRequiredService<TelemetryConnectionManager>().GetActiveMachineConnectionsForLease,
            sp.GetRequiredService<ILogger<TelemetryConnectionLeaseWorker>>(),
            sp.GetRequiredService<BackgroundWorkerHealthRegistry>()));
        services.AddHostedService(sp => sp.GetRequiredService<TelemetryConnectionManager>());

        services.AddHostedService<MercadoPagoWebhookWorker>();
        services.AddHostedService<MercadoPagoReconciliationWorker>();
        services.AddHostedService<OperationalAlertWorker>();
        services.AddHostedService<TelemetryDeadlineWorker>();
        services.AddHostedService<MdbStatusPollingWorker>();

        return services;
    }
}
