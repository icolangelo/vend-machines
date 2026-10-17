using System.Collections.Concurrent;

namespace VendingMachines.Api.Services;

public static class BackgroundWorkerNames
{
    public const string Telemetry = "telemetry-inbox";
    public static string TelemetryInboxConsumer(int number) => $"telemetry-inbox-consumer-{number}";
    public const string MercadoPagoWebhook = "mercado-pago-webhook";
    public const string MercadoPagoReconciliation = "mercado-pago-reconciliation";
    public const string OperationalAlerts = "operational-alerts";
    public const string TelemetryDeadlines = "telemetry-deadlines";
    public const string MdbStatusPolling = "mdb-status-polling";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Telemetry,
        TelemetryInboxConsumer(1), TelemetryInboxConsumer(2),
        TelemetryInboxConsumer(3), TelemetryInboxConsumer(4),
        MercadoPagoWebhook,
        MercadoPagoReconciliation,
        OperationalAlerts,
        TelemetryDeadlines,
        MdbStatusPolling
    };
}

public sealed record BackgroundWorkerHealthSnapshot(
    string Name,
    string Status,
    DateTime? StartedAt,
    DateTime? LastHeartbeatAt,
    DateTime? LastSuccessAt,
    int ConsecutiveFailures,
    string? LastErrorType);

/// <summary>Process-local diagnostics. This must not be used as an API readiness gate.</summary>
public sealed class BackgroundWorkerHealthRegistry
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, WorkerState> _workers = new(StringComparer.Ordinal);

    public BackgroundWorkerHealthRegistry()
    {
        foreach (var name in BackgroundWorkerNames.All)
            _workers.TryAdd(name, new WorkerState());
    }

    public void ReportStarted(string name)
    {
        var state = GetState(name);
        lock (state)
        {
            var now = DateTime.UtcNow;
            state.StartedAt = now;
            state.LastHeartbeatAt = now;
            state.Stopped = false;
        }
    }

    public void ReportHeartbeat(string name)
    {
        var state = GetState(name);
        lock (state) state.LastHeartbeatAt = DateTime.UtcNow;
    }

    public void ReportSuccess(string name)
    {
        var state = GetState(name);
        lock (state)
        {
            var now = DateTime.UtcNow;
            state.LastHeartbeatAt = now;
            state.LastSuccessAt = now;
            state.ConsecutiveFailures = 0;
            state.LastErrorType = null;
        }
    }

    public void ReportFailure(string name, Exception exception)
    {
        var state = GetState(name);
        lock (state)
        {
            state.LastHeartbeatAt = DateTime.UtcNow;
            state.ConsecutiveFailures++;
            state.LastErrorType = exception.GetType().Name;
        }
    }

    public void ReportStopped(string name)
    {
        var state = GetState(name);
        lock (state)
        {
            state.LastHeartbeatAt = DateTime.UtcNow;
            state.Stopped = true;
        }
    }

    public IReadOnlyList<BackgroundWorkerHealthSnapshot> GetSnapshots(DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        return _workers.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair =>
            {
                var state = pair.Value;
                lock (state)
                {
                    var status = state.Stopped ? "Stopped"
                        : state.StartedAt is null ? "NotStarted"
                        : state.LastHeartbeatAt is null || now - state.LastHeartbeatAt > StaleAfter ? "Stale"
                        : state.ConsecutiveFailures > 0 ? "Degraded"
                        : "Healthy";
                    return new BackgroundWorkerHealthSnapshot(
                        pair.Key,
                        status,
                        state.StartedAt,
                        state.LastHeartbeatAt,
                        state.LastSuccessAt,
                        state.ConsecutiveFailures,
                        state.LastErrorType);
                }
            })
            .ToArray();
    }

    private WorkerState GetState(string name) => _workers.GetOrAdd(name, _ => new WorkerState());

    private sealed class WorkerState
    {
        public DateTime? StartedAt;
        public DateTime? LastHeartbeatAt;
        public DateTime? LastSuccessAt;
        public int ConsecutiveFailures;
        public string? LastErrorType;
        public bool Stopped;
    }
}
