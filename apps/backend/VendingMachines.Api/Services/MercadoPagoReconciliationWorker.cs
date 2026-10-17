namespace VendingMachines.Api.Services;

public sealed class MercadoPagoReconciliationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MercadoPagoReconciliationWorker> _logger;
    private readonly BackgroundWorkerHealthRegistry? _health;

    public MercadoPagoReconciliationWorker(IServiceScopeFactory scopeFactory, ILogger<MercadoPagoReconciliationWorker> logger, BackgroundWorkerHealthRegistry? health = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _health = health;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _health?.ReportStarted(BackgroundWorkerNames.MercadoPagoReconciliation);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var processor = scope.ServiceProvider.GetRequiredService<MercadoPagoWebhookProcessor>();
                    await processor.ReconcilePendingPaymentsAsync(stoppingToken);
                    _health?.ReportSuccess(BackgroundWorkerNames.MercadoPagoReconciliation);
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    _health?.ReportFailure(BackgroundWorkerNames.MercadoPagoReconciliation, exception);
                    _logger.LogError(exception, "Erro no worker de conciliação de pagamentos Mercado Pago.");
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                }
            }
        }
        finally { _health?.ReportStopped(BackgroundWorkerNames.MercadoPagoReconciliation); }
    }
}
