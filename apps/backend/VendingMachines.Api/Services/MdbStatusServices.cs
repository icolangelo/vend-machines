using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;

namespace VendingMachines.Api.Services;

public sealed class MdbStatusOptions
{
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

public sealed class MdbStatusRequestService
{
    private readonly AppDbContext _db;
    private readonly TelemetryConnectionManager _connections;
    private readonly MdbStatusOptions _options;
    private readonly ILogger<MdbStatusRequestService> _logger;

    public MdbStatusRequestService(
        AppDbContext db,
        TelemetryConnectionManager connections,
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
        if (!_connections.IsOnline(machine.SerialNumber))
        {
            throw new InvalidOperationException("machine_offline");
        }

        var requestedAt = DateTime.UtcNow;
        state.MdbStatusRequestAt = requestedAt;
        await _db.SaveChangesAsync(cancellationToken);

        var result = await _connections.SendStatusRequestAsync(
            machine.SerialNumber,
            TimeSpan.FromSeconds(Math.Clamp(_options.ResponseTimeoutSeconds, 1, 30)),
            cancellationToken);

        state.MdbStatusRequestMessageId = result.MessageId == 0 ? null : result.MessageId;

        // Consultas periódicas bem-sucedidas são deliberadamente compactas. Um registro
        // completo a cada 30 segundos faria a tabela de telemetria crescer sem limite.
        if (!string.Equals(source, "poll", StringComparison.OrdinalIgnoreCase))
        {
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
        }
        else if (!result.Success)
        {
            _logger.LogDebug(
                "Consulta MDB sem resposta para {MachineId}: {Error}.",
                machineId,
                result.Error);
        }

        await _db.SaveChangesAsync(cancellationToken);
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

    public MdbStatusPollingWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<MdbStatusOptions> options,
        ILogger<MdbStatusPollingWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var machines = await GetDueMachinesAsync(stoppingToken);
                await Task.WhenAll(machines.Select(machine => RequestOneAsync(machine, stoppingToken)));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha no ciclo de consulta do status MDB.");
            }

            var jitter = Math.Max(0, _options.PollJitterSeconds);
            var delaySeconds = 1 + (jitter == 0 ? 0 : Random.Shared.Next(0, jitter + 1));
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
        }
    }

    private async Task<List<DueMachine>> GetDueMachinesAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var dueBefore = DateTime.UtcNow.AddSeconds(-Math.Clamp(_options.PollIntervalSeconds, 5, 3600));
        var batchSize = Math.Clamp(_options.BatchSize, 1, 200);

        var dueMachines = await db.MachineConnectionStates.AsNoTracking()
            .Where(x => x.MonitoringEnabled && x.IsOnline &&
                        (x.MdbStatusRequestAt == null || x.MdbStatusRequestAt <= dueBefore))
            .Join(
                db.Machines.AsNoTracking(),
                state => state.MachineId,
                machine => machine.Id,
                (state, machine) => new { MachineId = machine.Id, state.CompanyId })
            .OrderBy(x => x.MachineId)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        return dueMachines
            .Select(x => new DueMachine(x.MachineId, x.CompanyId))
            .ToList();
    }

    private async Task RequestOneAsync(DueMachine machine, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<MdbStatusRequestService>();
            await service.RequestAsync(
                machine.MachineId,
                machine.CompanyId,
                null,
                "poll",
                cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex.Message is "machine_offline" or "monitoring_inactive")
        {
            // O estado persistido pode mudar entre a seleção do lote e o envio.
        }
        catch (KeyNotFoundException)
        {
            // A máquina pode ter sido excluída entre a seleção do lote e o envio.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao consultar MDB da máquina {MachineId}.", machine.MachineId);
        }
    }

    private sealed record DueMachine(string MachineId, Guid CompanyId);
}
