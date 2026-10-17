using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;

namespace VendingMachines.Api.Services;

public sealed class TelemetryDeadlineWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TelemetryDeadlineWorker> _logger;
    private readonly BackgroundWorkerHealthRegistry? _health;

    public TelemetryDeadlineWorker(IServiceScopeFactory scopeFactory, ILogger<TelemetryDeadlineWorker> logger, BackgroundWorkerHealthRegistry? health = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _health = health;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _health?.ReportStarted(BackgroundWorkerNames.TelemetryDeadlines);
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            var lastCleanup = DateTime.MinValue;
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var orchestrator = scope.ServiceProvider.GetRequiredService<SessionOrchestrator>();
                    await orchestrator.ProcessDeadlinesAsync(stoppingToken);
                    await orchestrator.ProcessOutboxAsync(stoppingToken);

                    if (DateTime.UtcNow - lastCleanup >= TimeSpan.FromHours(1))
                    {
                        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                        var limit = DateTime.UtcNow.AddDays(-90);
                        await db.TelemetryEvents.Where(x => x.CreatedAt < limit).ExecuteDeleteAsync(stoppingToken);
                        await db.TelemetryCommands.Where(x => x.CreatedAt < limit).ExecuteDeleteAsync(stoppingToken);
                        lastCleanup = DateTime.UtcNow;
                    }
                    _health?.ReportSuccess(BackgroundWorkerNames.TelemetryDeadlines);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _health?.ReportFailure(BackgroundWorkerNames.TelemetryDeadlines, ex);
                    _logger.LogError(ex, "Falha no processamento de deadlines de telemetria.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { _health?.ReportStopped(BackgroundWorkerNames.TelemetryDeadlines); }
    }
}
