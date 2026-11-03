using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;

namespace VendingMachines.Api.Services;

public sealed class TelemetryConnectionLeaseWorker : BackgroundService
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DatabaseOperationTimeout = TimeSpan.FromSeconds(3);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly string _nodeId;
    private readonly Func<IReadOnlyDictionary<string, string>> _getActiveMachineConnections;
    private readonly ILogger<TelemetryConnectionLeaseWorker> _logger;
    private readonly BackgroundWorkerHealthRegistry? _health;

    public TelemetryConnectionLeaseWorker(
        IServiceScopeFactory scopeFactory,
        string nodeId,
        Func<IReadOnlyDictionary<string, string>> getActiveMachineConnections,
        ILogger<TelemetryConnectionLeaseWorker> logger,
        BackgroundWorkerHealthRegistry? health = null)
    {
        _scopeFactory = scopeFactory;
        _nodeId = nodeId;
        _getActiveMachineConnections = getActiveMachineConnections;
        _logger = logger;
        _health = health;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _health?.ReportStarted(BackgroundWorkerNames.TelemetryConnectionLease);
        await Task.Yield();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RenewLeasesAsync(stoppingToken);
                    _health?.ReportSuccess(BackgroundWorkerNames.TelemetryConnectionLease);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _health?.ReportFailure(BackgroundWorkerNames.TelemetryConnectionLease, ex);
                    _logger.LogError(
                        "Falha ao renovar os leases de telemetria do nó {NodeId}: {ErrorType}.",
                        _nodeId,
                        ex.GetType().Name);
                }

                try
                {
                    await Task.Delay(HeartbeatInterval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            await ExpireLeasesBestEffortAsync();
            _health?.ReportStopped(BackgroundWorkerNames.TelemetryConnectionLease);
        }
    }

    private async Task RenewLeasesAsync(CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(DatabaseOperationTimeout);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var activeConnections = _getActiveMachineConnections();
        if (activeConnections.Count == 0) return;

        var activeMachineIds = activeConnections.Keys.ToArray();
        var states = await db.MachineConnectionStates
            .Where(state => activeMachineIds.Contains(state.MachineId) &&
                            state.OwnerNodeId == _nodeId && state.IsOnline &&
                            state.OwnerConnectionId != null && state.OwnerConnectionId == state.ConnectionId)
            .ToListAsync(timeout.Token);
        var leaseExpiry = now.Add(LeaseDuration);
        foreach (var state in states)
        {
            if (activeConnections.TryGetValue(state.MachineId, out var activeConnectionId) &&
                string.Equals(activeConnectionId, state.ConnectionId, StringComparison.Ordinal))
            {
                state.OwnerLeaseExpiresAt = leaseExpiry;
            }
        }

        await db.SaveChangesAsync(timeout.Token);
    }

    private async Task ExpireLeasesBestEffortAsync()
    {
        using var timeout = new CancellationTokenSource(DatabaseOperationTimeout);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            await db.MachineConnectionStates
                .Where(state => state.OwnerNodeId == _nodeId && state.IsOnline)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(state => state.OwnerLeaseExpiresAt, now), timeout.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Os leases do nó {NodeId} expirarão sem atualização explícita: {ErrorType}.",
                _nodeId,
                ex.GetType().Name);
        }
    }
}
