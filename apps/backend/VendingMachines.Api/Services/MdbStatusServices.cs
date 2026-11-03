using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;

namespace VendingMachines.Api.Services;

public sealed class MdbStatusOptions
{
    public const int MaximumPollJitterSeconds = 60;

    public int PollIntervalSeconds { get; set; } = 30;
    public int StaleAfterSeconds { get; set; } = 90;
    public int ResponseTimeoutSeconds { get; set; } = 5;
    public int AutoOpenCooldownSeconds { get; set; } = 30;
    public int PollJitterSeconds { get; set; } = 5;
    public int BatchSize { get; set; } = 25;
    public bool EnforceOnManualStart { get; set; }
}

public sealed record MdbStatusRequestOutcome(
    bool Success,
    int MessageId,
    string? Status,
    DateTime? ResponseAt,
    string? Error);

public static class MdbStatusPollClaim
{
    public static IQueryable<MachineConnectionState> GetDueStates(
        AppDbContext db,
        DateTime dueBefore,
        DateTime onlineAfter,
        DateTime routeValidAfter) => db.MachineConnectionStates.AsNoTracking()
        .Where(x => x.MonitoringEnabled && x.IsOnline && x.LastSeenAt >= onlineAfter &&
                    x.OwnerNodeId != null && x.OwnerNodeId != "" &&
                    x.ConnectionId != null && x.ConnectionId != "" &&
                    x.OwnerConnectionId == x.ConnectionId &&
                    x.CommandRoutingVersion == TelemetryConnectionManager.CurrentCommandRoutingVersion &&
                    x.OwnerLeaseExpiresAt > routeValidAfter &&
                    (x.MdbStatusRequestAt == null || x.MdbStatusRequestAt <= dueBefore));

    public static async Task<bool> TryClaimAsync(
        AppDbContext db,
        string machineId,
        Guid companyId,
        DateTime requestedAt,
        int pollIntervalSeconds,
        CancellationToken cancellationToken)
    {
        var dueBefore = requestedAt.AddSeconds(-Math.Clamp(pollIntervalSeconds, 5, 3600));
        var claimed = await db.MachineConnectionStates
            .Where(x => x.MachineId == machineId && x.CompanyId == companyId &&
                        x.MonitoringEnabled && x.IsOnline &&
                        x.OwnerNodeId != null && x.OwnerNodeId != "" &&
                        x.ConnectionId != null && x.ConnectionId != "" &&
                        x.OwnerConnectionId == x.ConnectionId &&
                        x.CommandRoutingVersion == TelemetryConnectionManager.CurrentCommandRoutingVersion &&
                        x.OwnerLeaseExpiresAt > requestedAt &&
                        (x.MdbStatusRequestAt == null || x.MdbStatusRequestAt <= dueBefore))
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.MdbStatusRequestAt, requestedAt), cancellationToken);

        return claimed == 1;
    }
}

public sealed class MdbStatusRequestService
{
    private readonly AppDbContext _db;
    private readonly ITelemetryConnectionOperations _connections;
    private readonly MdbStatusOptions _options;
    private readonly ILogger<MdbStatusRequestService> _logger;

    public MdbStatusRequestService(
        AppDbContext db,
        ITelemetryConnectionOperations connections,
        IOptions<MdbStatusOptions> options,
        ILogger<MdbStatusRequestService> logger)
    {
        _db = db;
        _connections = connections;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<MdbStatusRequestOutcome> RequestAsync(
        string machineId,
        Guid companyId,
        Guid? userId,
        string source,
        CancellationToken cancellationToken)
    {
        var machine = await _db.Machines.FirstOrDefaultAsync(
            x => x.Id == machineId && x.CompanyId == companyId,
            cancellationToken)
            ?? throw new KeyNotFoundException("Máquina não encontrada.");

        var state = await _db.MachineConnectionStates.FirstOrDefaultAsync(
            x => x.MachineId == machineId && x.CompanyId == companyId,
            cancellationToken);
        if (state?.MonitoringEnabled != true)
        {
            throw new InvalidOperationException("monitoring_inactive");
        }
        if (!await _connections.IsOnlineAsync(machine.SerialNumber, cancellationToken))
        {
            throw new InvalidOperationException("machine_offline");
        }

        var isPoll = string.Equals(source, "poll", StringComparison.OrdinalIgnoreCase);
        var requestedAt = DateTime.UtcNow;
        if (isPoll)
        {
            var claimed = await MdbStatusPollClaim.TryClaimAsync(
                _db,
                machineId,
                companyId,
                requestedAt,
                _options.PollIntervalSeconds,
                cancellationToken);
            if (!claimed) throw new InvalidOperationException("mdb_status_poll_not_due");
        }
        else
        {
            state.MdbStatusRequestAt = requestedAt;
            await _db.SaveChangesAsync(cancellationToken);
        }

        var result = await _connections.SendStatusRequestAsync(
            machine.SerialNumber,
            TimeSpan.FromSeconds(Math.Clamp(_options.ResponseTimeoutSeconds, 1, 30)),
            cancellationToken);

        int? messageId = result.MessageId == 0 ? null : result.MessageId;

        // Consultas periódicas bem-sucedidas são deliberadamente compactas. Um registro
        // completo a cada 30 segundos faria a tabela de telemetria crescer sem limite.
        if (!isPoll)
        {
            state.MdbStatusRequestMessageId = messageId;
            _db.TelemetryCommands.Add(new TelemetryCommand
            {
                CompanyId = companyId,
                MachineId = machineId,
                UserId = userId,
                MessageId = result.MessageId,
                Command = "MDB_STATUS",
                Status = result.Success ? "responded" : result.Error ?? "failed",
                Attempts = result.MessageId == 0 ? 0 : 1,
                SentAt = requestedAt,
                Error = result.Error
            });
            await _db.SaveChangesAsync(cancellationToken);
        }
        else
        {
            await _db.MachineConnectionStates
                .Where(x => x.MachineId == machineId && x.CompanyId == companyId &&
                            x.MdbStatusRequestAt == requestedAt)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.MdbStatusRequestMessageId, messageId), cancellationToken);
            if (!result.Success)
            {
                _logger.LogDebug(
                    "Consulta MDB sem resposta para {MachineId}: {Error}.",
                    machineId,
                    result.Error);
            }
        }
        return new MdbStatusRequestOutcome(
            result.Success,
            result.MessageId,
            result.Status,
            result.ResponseAt,
            result.Error);
    }
}

public sealed class MdbStatusPollingWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MdbStatusOptions _options;
    private readonly ILogger<MdbStatusPollingWorker> _logger;
    private readonly BackgroundWorkerHealthRegistry? _health;

    public MdbStatusPollingWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<MdbStatusOptions> options,
        ILogger<MdbStatusPollingWorker> logger,
        BackgroundWorkerHealthRegistry? health = null)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
        _health = health;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _health?.ReportStarted(BackgroundWorkerNames.MdbStatusPolling);
        await Task.Yield();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var machines = await GetDueMachinesAsync(stoppingToken);
                    var results = await Task.WhenAll(machines.Select(machine => RequestOneAsync(machine, stoppingToken)));
                    if (results.Any(result => result == PollRequestResult.Failed))
                    {
                        _health?.ReportFailure(BackgroundWorkerNames.MdbStatusPolling, new InvalidOperationException("mdb_machine_request_failed"));
                    }
                    else if (results.Any(result => result == PollRequestResult.Succeeded))
                    {
                        _health?.ReportSuccess(BackgroundWorkerNames.MdbStatusPolling);
                    }
                    else
                    {
                        _health?.ReportHeartbeat(BackgroundWorkerNames.MdbStatusPolling);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _health?.ReportFailure(BackgroundWorkerNames.MdbStatusPolling, ex);
                    _logger.LogError(ex, "Falha no ciclo de consulta do status MDB.");
                }

                var jitter = Math.Clamp(_options.PollJitterSeconds, 0, MdbStatusOptions.MaximumPollJitterSeconds);
                var delaySeconds = 1 + (jitter == 0 ? 0 : Random.Shared.Next(0, jitter + 1));
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { _health?.ReportStopped(BackgroundWorkerNames.MdbStatusPolling); }
    }

    private async Task<List<DueMachine>> GetDueMachinesAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var dueBefore = now.AddSeconds(-Math.Clamp(_options.PollIntervalSeconds, 5, 3600));
        var onlineAfter = now - MachineConnectionFreshness.Window;
        var batchSize = Math.Clamp(_options.BatchSize, 1, 200);

        var dueMachines = await MdbStatusPollClaim.GetDueStates(db, dueBefore, onlineAfter, now)
            .Join(
                db.Machines.AsNoTracking(),
                state => state.MachineId,
                machine => machine.Id,
                (state, machine) => new
                {
                    MachineId = machine.Id,
                    state.CompanyId,
                    MachineCompanyId = machine.CompanyId
                })
            .Where(item => item.CompanyId == item.MachineCompanyId)
            .OrderBy(x => x.MachineId)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        return dueMachines
            .Select(x => new DueMachine(x.MachineId, x.CompanyId))
            .ToList();
    }

    private async Task<PollRequestResult> RequestOneAsync(DueMachine machine, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<MdbStatusRequestService>();
            var outcome = await service.RequestAsync(
                machine.MachineId,
                machine.CompanyId,
                null,
                "poll",
                cancellationToken);
            return outcome.Success ? PollRequestResult.Succeeded : PollRequestResult.Failed;
        }
        catch (InvalidOperationException ex) when (ex.Message is "machine_offline" or "monitoring_inactive" or "mdb_status_poll_not_due")
        {
            // O estado persistido pode mudar entre a seleção do lote e o envio.
            return PollRequestResult.Skipped;
        }
        catch (KeyNotFoundException)
        {
            // A máquina pode ter sido excluída entre a seleção do lote e o envio.
            return PollRequestResult.Skipped;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao consultar MDB da máquina {MachineId}.", machine.MachineId);
            return PollRequestResult.Failed;
        }
    }

    private enum PollRequestResult
    {
        Succeeded,
        Skipped,
        Failed
    }

    private sealed record DueMachine(string MachineId, Guid CompanyId);
}
