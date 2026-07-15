using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;

namespace VendingMachines.Api.Services;

public sealed class TelemetryDeadlineWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TelemetryDeadlineWorker> _logger;

    public TelemetryDeadlineWorker(IServiceScopeFactory scopeFactory, ILogger<TelemetryDeadlineWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
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
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { _logger.LogError(ex, "Falha no processamento de deadlines de telemetria."); }
        }
    }
}
