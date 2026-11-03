using System.Text;
using System.Text.Json;
using Npgsql;

namespace VendingMachines.Api.Services;

public sealed class PostgresTelemetryPanelEventBus
{
    private const string Channel = "vend_telemetry_panel";
    private const int MaximumNotificationBytes = 7900;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PostgresTelemetryPanelEventBus> _logger;

    public PostgresTelemetryPanelEventBus(
        NpgsqlDataSource dataSource,
        ILogger<PostgresTelemetryPanelEventBus> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public async Task PublishAsync(
        Guid companyId,
        string? machineId,
        string payloadJson,
        string originNodeId,
        CancellationToken cancellationToken)
    {
        var notification = new TelemetryPanelHub.PanelNotification(companyId, machineId, payloadJson, originNodeId);
        var serialized = JsonSerializer.Serialize(notification, JsonOptions);
        if (Encoding.UTF8.GetByteCount(serialized) > MaximumNotificationBytes)
        {
            var refreshPayload = JsonSerializer.Serialize(new
            {
                type = "refresh.required",
                machineId
            }, JsonOptions);
            serialized = JsonSerializer.Serialize(
                notification with { PayloadJson = refreshPayload }, JsonOptions);
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_notify(@channel, @payload)";
        command.Parameters.AddWithValue("channel", Channel);
        command.Parameters.AddWithValue("payload", serialized);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ListenAsync(
        Action<string> onNotification,
        CancellationToken cancellationToken,
        BackgroundWorkerHealthRegistry? health = null)
    {
        var workerName = BackgroundWorkerNames.TelemetryPanelEventRelay;
        health?.ReportStarted(workerName);
        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = health is null
            ? Task.CompletedTask
            : KeepHealthHeartbeatAsync(health, workerName, heartbeatCancellation.Token);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
                    connection.Notification += (_, notification) =>
                    {
                        if (notification.Channel == Channel)
                            onNotification(notification.Payload);
                    };

                    await using (var listen = connection.CreateCommand())
                    {
                        listen.CommandText = "LISTEN vend_telemetry_panel";
                        await listen.ExecuteNonQueryAsync(cancellationToken);
                    }

                    health?.ReportSuccess(workerName);
                    while (!cancellationToken.IsCancellationRequested)
                        await connection.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    health?.ReportFailure(workerName, ex);
                    _logger.LogError(ex, "Listener PostgreSQL de eventos do painel perdeu a conexão; nova tentativa em 3 segundos.");
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                }
            }
        }
        finally
        {
            heartbeatCancellation.Cancel();
            try
            {
                await heartbeatTask;
            }
            catch (OperationCanceledException) when (heartbeatCancellation.IsCancellationRequested) { }

            health?.ReportStopped(workerName);
        }
    }

    private static async Task KeepHealthHeartbeatAsync(
        BackgroundWorkerHealthRegistry health,
        string workerName,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(cancellationToken))
            health.ReportHeartbeat(workerName);
    }
}

public sealed class TelemetryPanelEventRelay : BackgroundService
{
    private readonly PostgresTelemetryPanelEventBus _eventBus;
    private readonly TelemetryPanelHub _panelHub;
    private readonly BackgroundWorkerHealthRegistry _health;

    public TelemetryPanelEventRelay(
        PostgresTelemetryPanelEventBus eventBus,
        TelemetryPanelHub panelHub,
        BackgroundWorkerHealthRegistry health)
    {
        _eventBus = eventBus;
        _panelHub = panelHub;
        _health = health;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        _eventBus.ListenAsync(_panelHub.BroadcastRemoteNotification, stoppingToken, _health);
}
