using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;

namespace VendingMachines.Api.Services;

public sealed class MercadoPagoWebhookWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MercadoPagoWebhookWorker> _logger;
    private readonly BackgroundWorkerHealthRegistry? _health;

    public MercadoPagoWebhookWorker(IServiceScopeFactory scopeFactory, ILogger<MercadoPagoWebhookWorker> logger, BackgroundWorkerHealthRegistry? health = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _health = health;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _health?.ReportStarted(BackgroundWorkerNames.MercadoPagoWebhook);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var processor = scope.ServiceProvider.GetRequiredService<MercadoPagoWebhookProcessor>();
                    var didWork = await processor.ProcessNextAsync(stoppingToken);
                    _health?.ReportSuccess(BackgroundWorkerNames.MercadoPagoWebhook);
                    if (!didWork)
                        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    _health?.ReportFailure(BackgroundWorkerNames.MercadoPagoWebhook, exception);
                    _logger.LogError(exception, "Erro no worker de webhooks do Mercado Pago.");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
        }
        finally { _health?.ReportStopped(BackgroundWorkerNames.MercadoPagoWebhook); }
    }
}
