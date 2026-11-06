using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;

namespace VendingMachines.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/telemetry")]
public sealed class TelemetryController : BaseApiController
{
    private readonly AppDbContext _db;
    private readonly SessionOrchestrator _orchestrator;
    private readonly TelemetryConnectionManager _connections;
    private readonly TelemetryPanelHub _panelHub;
    private readonly WebSocketTicketService _tickets;
    private readonly MdbStatusRequestService _mdbStatusRequests;
    private readonly MdbStatusOptions _mdbOptions;

    public TelemetryController(
        AppDbContext db,
        SessionOrchestrator orchestrator,
        TelemetryConnectionManager connections,
        TelemetryPanelHub panelHub,
        WebSocketTicketService tickets,
        MdbStatusRequestService mdbStatusRequests,
        Microsoft.Extensions.Options.IOptions<MdbStatusOptions> mdbOptions)
    {
        _db = db;
        _orchestrator = orchestrator;
        _connections = connections;
        _panelHub = panelHub;
        _tickets = tickets;
        _mdbStatusRequests = mdbStatusRequests;
        _mdbOptions = mdbOptions.Value;
    }

    [HttpGet("connections")]
    public async Task<IActionResult> GetConnections(
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery] bool? monitoring = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetIdentity(out _, out var companyId)) return BadRequest(new { message = "Usuário sem empresa." });
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = _db.Machines.AsNoTracking().Where(x => x.CompanyId == companyId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(x => x.Name.ToLower().Contains(term) || x.SerialNumber.ToLower().Contains(term));
        }

        var total = await query.CountAsync(cancellationToken);
        var machines = await query.OrderBy(x => x.Name).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var ids = machines.Select(x => x.Id).ToArray();
        var states = await _db.MachineConnectionStates.AsNoTracking().Where(x => ids.Contains(x.MachineId))
            .ToDictionaryAsync(x => x.MachineId, cancellationToken);
        var sessions = await _db.MachineSessions.AsNoTracking()
            .Where(x => ids.Contains(x.MachineId) && x.ClosedAt == null)
            .ToDictionaryAsync(x => x.MachineId, cancellationToken);

        var now = DateTime.UtcNow;
        var staleAfter = TimeSpan.FromSeconds(Math.Clamp(_mdbOptions.StaleAfterSeconds, 10, 3600));
        var items = machines.Select(machine =>
        {
            states.TryGetValue(machine.Id, out var connection);
            sessions.TryGetValue(machine.Id, out var session);
            return new
            {
                machineId = machine.Id,
                machineName = machine.Name,
                serialNumber = machine.SerialNumber,
                location = machine.Location,
                online = connection?.IsOnline == true && _connections.IsOnline(machine.SerialNumber),
                monitoringEnabled = connection?.MonitoringEnabled == true,
                autoOpenSessionEnabled = machine.AutoOpenSessionEnabled,
                manualStartRequiresMdb = _mdbOptions.EnforceOnManualStart,
                connectedAt = connection?.ConnectedAt,
                disconnectedAt = connection?.DisconnectedAt,
                lastSeenAt = connection?.LastSeenAt,
                mdbStatus = connection?.MdbStatus,
                mdbStatusUpdatedAt = connection?.MdbStatusUpdatedAt,
                mdbStatusFresh = connection?.MdbStatus != null &&
                                 connection.MdbStatusUpdatedAt >= now - staleAfter &&
                                 connection.IsOnline &&
                                 _connections.IsOnline(machine.SerialNumber),
                autoOpenLastAttemptAt = connection?.AutoOpenLastAttemptAt,
                autoOpenLastError = connection?.AutoOpenLastError,
                signal = (int?)null,
                activeSession = session == null ? null : new
                {
                    sessionId = session.Id,
                    transactionId = session.TransactionId,
                    state = session.State,
                    itemNumber = session.ItemNumber,
                    amountCents = session.AmountCents,
                    closeReason = session.CloseReason,
                    selectionDeadlineAt = session.SelectionDeadlineAt,
                    paymentDeadlineAt = session.PaymentDeadlineAt,
                    deliveryDeadlineAt = session.DeliveryDeadlineAt,
                    startedAt = session.StartedAt
                }
            };
        }).Where(x => status == null || (status == "online" ? x.online : status != "offline" || !x.online))
          .Where(x => monitoring == null || x.monitoringEnabled == monitoring.Value)
          .ToList();

        return Ok(new { items, page, pageSize, totalItems = total, totalPages = (int)Math.Ceiling(total / (double)pageSize) });
    }

    [HttpPut("machines/{machineId}/monitoring")]
    public async Task<IActionResult> SetMonitoring(string machineId, [FromBody] MonitoringRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var userId, out var companyId)) return BadRequest(new { message = "Usuário sem empresa." });
        var machine = await _db.Machines.FirstOrDefaultAsync(x => x.Id == machineId && x.CompanyId == companyId, cancellationToken);
        if (machine == null) return NotFound();

        var state = await _db.MachineConnectionStates.FirstOrDefaultAsync(x => x.MachineId == machineId, cancellationToken);
        if (state == null)
        {
            state = new MachineConnectionState { MachineId = machineId, CompanyId = companyId };
            _db.MachineConnectionStates.Add(state);
        }
        state.MonitoringEnabled = request.Enabled;
        if (request.Enabled)
        {
            state.ActivatedAt = DateTime.UtcNow;
            state.ActivatedByUserId = userId;
        }
        else
        {
            state.DeactivatedAt = DateTime.UtcNow;
            state.DeactivatedByUserId = userId;
        }
        _db.TelemetryEvents.Add(new TelemetryEvent
        {
            CompanyId = companyId,
            MachineId = machineId,
            UserId = userId,
            EventType = request.Enabled ? "monitoring.activated" : "monitoring.deactivated"
        });
        await _db.SaveChangesAsync(cancellationToken);
        await _panelHub.BroadcastCompanyAsync(companyId, new { type = "monitoring.updated", machineId, enabled = request.Enabled });
        return Ok(new { machineId, monitoringEnabled = request.Enabled });
    }

    [HttpPost("machines/{machineId}/sessions")]
    public async Task<IActionResult> StartSession(string machineId, [FromBody] StartSessionRequest? request, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var userId, out var companyId)) return BadRequest(new { message = "Usuário sem empresa." });
        var state = await _db.MachineConnectionStates.AsNoTracking().FirstOrDefaultAsync(
            x => x.MachineId == machineId && x.CompanyId == companyId, cancellationToken);
        if (state?.MonitoringEnabled != true) return Conflict(new { code = "monitoring_inactive", message = "Ative o acompanhamento da máquina." });
        try
        {
            var session = await _orchestrator.StartSessionAsync(machineId, companyId, userId, "panel", cancellationToken);
            return CreatedAtAction(nameof(GetSession), new { sessionId = session.Id }, session);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return Conflict(new { code = ex.Message, message = MapError(ex.Message) }); }
    }

    [HttpPost("machines/{machineId}/mdb-status")]
    public async Task<IActionResult> RequestMdbStatus(string machineId, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var userId, out var companyId))
            return BadRequest(new { message = "Usuário sem empresa." });

        try
        {
            var result = await _mdbStatusRequests.RequestAsync(
                machineId,
                companyId,
                userId,
                "panel",
                cancellationToken);
            if (!result.Success)
            {
                return StatusCode(StatusCodes.Status504GatewayTimeout, new
                {
                    code = result.Error,
                    message = MapError(result.Error ?? "mdb_status_unavailable")
                });
            }

            return Ok(new
            {
                machineId,
                messageId = result.MessageId,
                mdbStatus = result.Status,
                mdbStatusUpdatedAt = result.ResponseAt
            });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { code = ex.Message, message = MapError(ex.Message) });
        }
    }

    [HttpGet("sessions/{sessionId:guid}")]
    public async Task<IActionResult> GetSession(Guid sessionId, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out _, out var companyId)) return BadRequest(new { message = "Usuário sem empresa." });
        var session = await _db.MachineSessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sessionId && x.CompanyId == companyId, cancellationToken);
        return session == null ? NotFound() : Ok(session);
    }

    [HttpPost("sessions/{sessionId:guid}/cancel")]
    public async Task<IActionResult> CancelSession(Guid sessionId, [FromBody] CancelSessionRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var userId, out var companyId)) return BadRequest(new { message = "Usuário sem empresa." });
        try
        {
            var session = await _orchestrator.CancelSessionAsync(sessionId, companyId, userId, request.Reason ?? "user_cancelled", cancellationToken);
            return Accepted(new { sessionId, state = session.State });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return Conflict(new { code = ex.Message, message = MapError(ex.Message) }); }
    }

    [HttpPost("machines/{machineId}/commands")]
    public async Task<IActionResult> SendCommand(string machineId, [FromBody] SendCommandRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var userId, out var companyId)) return BadRequest(new { message = "Usuário sem empresa." });
        var machine = await _db.Machines.FirstOrDefaultAsync(x => x.Id == machineId && x.CompanyId == companyId, cancellationToken);
        if (machine == null) return NotFound();
        var monitoring = await _db.MachineConnectionStates.AsNoTracking().FirstOrDefaultAsync(x => x.MachineId == machineId, cancellationToken);
        if (monitoring?.MonitoringEnabled != true) return Conflict(new { code = "monitoring_inactive", message = "Ative o acompanhamento da máquina." });
        if (string.IsNullOrWhiteSpace(request.Command)) return BadRequest(new { message = "Comando obrigatório." });

        var result = await _connections.SendCommandAsync(machine.SerialNumber, request.Data ?? request.Command, cancellationToken);
        var command = new TelemetryCommand
        {
            CompanyId = companyId,
            MachineId = machineId,
            UserId = userId,
            MessageId = result.MessageId,
            Command = request.Command,
            DataJson = request.Data == null ? null : System.Text.Json.JsonSerializer.Serialize(request.Data),
            Status = result.Success ? "acknowledged" : "failed",
            Attempts = result.Attempts,
            SentAt = DateTime.UtcNow,
            AckAt = result.Success ? DateTime.UtcNow : null,
            Error = result.Error
        };
        _db.TelemetryCommands.Add(command);
        await _db.SaveChangesAsync(cancellationToken);
        if (!result.Success) return result.Error == "machine_offline"
            ? Conflict(new { code = result.Error, message = "Máquina offline." })
            : StatusCode(504, new { code = result.Error, message = "A máquina não confirmou o comando." });
        return Accepted(new { commandId = command.Id, messageId = result.MessageId, status = command.Status });
    }

    [HttpGet("events")]
    public async Task<IActionResult> GetEvents([FromQuery] string? machineId, [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken cancellationToken = default)
    {
        if (!TryGetIdentity(out _, out var companyId)) return BadRequest(new { message = "Usuário sem empresa." });
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var query = _db.TelemetryEvents.AsNoTracking().Where(x => x.CompanyId == companyId);
        if (!string.IsNullOrWhiteSpace(machineId)) query = query.Where(x => x.MachineId == machineId);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(x => x.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return Ok(new { items, page, pageSize, totalItems = total, totalPages = (int)Math.Ceiling(total / (double)pageSize) });
    }

    [HttpPost("socket-ticket")]
    public IActionResult CreateSocketTicket()
    {
        if (!TryGetIdentity(out var userId, out var companyId)) return BadRequest(new { message = "Usuário sem empresa." });
        var ticket = _tickets.Issue(userId, companyId);
        return Ok(new { ticket = ticket.Token, expiresAt = ticket.ExpiresAt });
    }

    private bool TryGetIdentity(out Guid userId, out Guid companyId)
    {
        var hasUser = Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out userId);
        var hasCompany = TryGetEffectiveCompanyId(out companyId);
        return hasUser && hasCompany;
    }

    private static string MapError(string code) => code switch
    {
        "machine_offline" => "A máquina está offline.",
        "monitoring_inactive" => "Ative o acompanhamento da máquina.",
        "session_already_active" => "A máquina já possui uma sessão ativa.",
        "mdb_status_unavailable" => "O status MDB ainda não está disponível.",
        "mdb_status_stale" => "O status MDB está desatualizado. Consulte novamente.",
        "mdb_not_ready" => "O MDB não está disponível para abrir uma sessão.",
        "response_timeout" => "A máquina não respondeu à consulta MDB.",
        "invalid_mdb_status" => "A máquina retornou um status MDB desconhecido.",
        "automatic_session_disabled" => "A abertura automática está desativada.",
        "payment_already_approved" => "O pagamento já foi aprovado e não pode ser cancelado desta forma.",
        _ => "Não foi possível concluir a operação."
    };
}

public sealed class MonitoringRequest { public bool Enabled { get; set; } }
public sealed class StartSessionRequest { public string? Source { get; set; } }
public sealed class CancelSessionRequest { public string? Reason { get; set; } }
public sealed class SendCommandRequest
{
    public string Command { get; set; } = string.Empty;
    public object? Data { get; set; }
}
