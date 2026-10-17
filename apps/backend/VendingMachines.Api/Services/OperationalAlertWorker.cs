namespace VendingMachines.Api.Services;

public sealed class OperationalAlertWorker(IServiceScopeFactory scopeFactory, ILogger<OperationalAlertWorker> logger, BackgroundWorkerHealthRegistry? health = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        health?.ReportStarted(BackgroundWorkerNames.OperationalAlerts);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var sync = scope.ServiceProvider.GetRequiredService<OperationalAlertSyncService>();
                    await sync.SyncAsync(stoppingToken);
                    health?.ReportSuccess(BackgroundWorkerNames.OperationalAlerts);
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    health?.ReportFailure(BackgroundWorkerNames.OperationalAlerts, exception);
                    logger.LogError(exception, "Falha ao sincronizar alertas operacionais.");
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                }
            }
        }
        finally { health?.ReportStopped(BackgroundWorkerNames.OperationalAlerts); }
    }
}
