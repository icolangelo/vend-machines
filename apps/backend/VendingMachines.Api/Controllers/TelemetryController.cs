using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
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
    private readonly ITelemetryConnectionOperations _connections;
    private readonly ITelemetryCommandSender _commandSender;
    private readonly TelemetryPanelHub _panelHub;
    private readonly WebSocketTicketService _tickets;
    private readonly MdbStatusRequestService _mdbStatusRequests;
    private readonly MdbStatusOptions _mdbOptions;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly LegacyTelemetryRequestRelay? _legacyTelemetryRelay;

    public TelemetryController(
        AppDbContext db,
        SessionOrchestrator orchestrator,
        ITelemetryConnectionOperations connections,
        TelemetryPanelHub panelHub,
        WebSocketTicketService tickets,
        MdbStatusRequestService mdbStatusRequests,
        Microsoft.Extensions.Options.IOptions<MdbStatusOptions> mdbOptions,
        ITelemetryCommandSender? commandSender = null,
        IServiceScopeFactory? scopeFactory = null,
        LegacyTelemetryRequestRelay? legacyTelemetryRelay = null)
    {
        _db = db;
        _orchestrator = orchestrator;
        _connections = connections;
        _commandSender = commandSender ?? connections;
        _panelHub = panelHub;
        _tickets = tickets;
        _mdbStatusRequests = mdbStatusRequests;
        _mdbOptions = mdbOptions.Value;
        _scopeFactory = scopeFactory;
        _legacyTelemetryRelay = legacyTelemetryRelay;
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
        var states = await _db.MachineConnectionStates.AsNoTracking()
            .Where(x => x.CompanyId == companyId && ids.Contains(x.MachineId))
            .ToDictionaryAsync(x => x.MachineId, cancellationToken);
        var sessions = await _db.MachineSessions.AsNoTracking()
            .Where(x => x.CompanyId == companyId && ids.Contains(x.MachineId) && x.ClosedAt == null)
            .ToDictionaryAsync(x => x.MachineId, cancellationToken);

        var now = DateTime.UtcNow;
        var connectionFreshnessCutoff = now - MachineConnectionFreshness.Window;
        var staleAfter = TimeSpan.FromSeconds(Math.Clamp(_mdbOptions.StaleAfterSeconds, 10, 3600));
        var onlineSerials = await _connections.GetOnlineSerialsAsync(
            machines.Select(machine => machine.SerialNumber), cancellationToken);
        var legacyRelayConfigured = _legacyTelemetryRelay?.IsConfigured == true;
        var items = machines.Select(machine =>
        {
            states.TryGetValue(machine.Id, out var connection);
            sessions.TryGetValue(machine.Id, out var session);
            var online = connection?.IsOnline == true &&
                         connection.LastSeenAt >= connectionFreshnessCutoff &&
                         onlineSerials.Contains(machine.SerialNumber.Trim().ToUpperInvariant());
            return new
            {
                machineId = machine.Id,
                machineName = machine.Name,
                serialNumber = machine.SerialNumber,
                location = machine.Location,
                online,
                commandRouteAvailable = online && connection != null &&
                                        ((!string.IsNullOrWhiteSpace(connection.ConnectionId) &&
                                          !string.IsNullOrWhiteSpace(connection.OwnerNodeId) &&
                                          connection.OwnerConnectionId == connection.ConnectionId &&
                                          connection.CommandRoutingVersion == TelemetryConnectionManager.CurrentCommandRoutingVersion &&
                                          (connection.OwnerNodeId != _connections.NodeId ||
                                           _connections.IsLocalConnectionOnline(machine.SerialNumber, connection.ConnectionId))) ||
                                         (legacyRelayConfigured &&
                                          string.IsNullOrWhiteSpace(connection.OwnerNodeId) &&
                                          string.IsNullOrWhiteSpace(connection.OwnerConnectionId) &&
                                          connection.CommandRoutingVersion == null &&
                                          !string.IsNullOrWhiteSpace(connection.ConnectionId))),
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
                                 online,
                mdbMenuSelectionActive = online && connection?.MdbStatus == MdbStatuses.Inactive &&
                                         connection.MdbMenuPhase == "inactive" &&
                                         connection.MdbMenuConnectionId == connection.ConnectionId &&
                                         connection.MdbMenuUpdatedAt >= now - staleAfter,
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

    [Authorize(Roles = AppRoles.CompanyAdminOrPlatformAdmin)]
    [HttpPut("machines/{machineId}/monitoring")]
    public async Task<IActionResult> SetMonitoring(string machineId, [FromBody] MonitoringRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var userId, out var companyId)) return BadRequest(new { message = "Usuário sem empresa." });
        var machine = await _db.Machines.FirstOrDefaultAsync(x => x.Id == machineId && x.CompanyId == companyId, cancellationToken);
        if (machine == null) return NotFound();

        var state = await _db.MachineConnectionStates.FirstOrDefaultAsync(
            x => x.MachineId == machineId,
            cancellationToken);
        if (state != null && state.CompanyId != companyId)
            return Conflict(new { code = "machine_state_company_mismatch", message = "Não foi possível atualizar o acompanhamento desta máquina." });
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

    [Authorize(Roles = AppRoles.CompanyAdminOrPlatformAdmin)]
    [HttpPost("machines/{machineId}/sessions")]
    public async Task<IActionResult> StartSession(string machineId, [FromBody] StartSessionRequest? request, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var userId, out var companyId)) return BadRequest(new { message = "Usuário sem empresa." });
        var relayed = await TryGetLegacyOwnerRelayResultAsync(
            machineId,
            companyId,
            $"api/telemetry/machines/{Uri.EscapeDataString(machineId)}/sessions",
            request,
            cancellationToken);
        if (relayed is not null)
        {
            if (IsRelayErrorCode(relayed, "session_already_active"))
            {
                var existingSession = await ExistingPanelSessionResultAsync(
                    machineId, companyId, userId, cancellationToken);
                if (existingSession is not null) return existingSession;
            }

            return ToRelayActionResult(relayed);
        }
        var state = await _db.MachineConnectionStates.AsNoTracking().FirstOrDefaultAsync(
            x => x.MachineId == machineId && x.CompanyId == companyId, cancellationToken);
        if (state?.MonitoringEnabled != true) return Conflict(new { code = "monitoring_inactive", message = "Ative o acompanhamento da máquina." });
        try
        {
            var session = await _orchestrator.StartSessionAsync(machineId, companyId, userId, "panel", cancellationToken);
            return CreatedAtAction(nameof(GetSession), new { sessionId = session.Id }, session);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) when (ex.Message is "machine_owner_unknown" or "deployment_draining" or "session_admission_gate_unavailable")
        {
            return AdmissionUnavailable(ex.Message);
        }
        catch (InvalidOperationException ex) { return Conflict(new { code = ex.Message, message = MapError(ex.Message) }); }
    }

    [Authorize(Roles = AppRoles.CompanyAdminOrPlatformAdmin)]
    [HttpPost("machines/{machineId}/mdb-status")]
    public async Task<IActionResult> RequestMdbStatus(string machineId, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var userId, out var companyId))
            return BadRequest(new { message = "Usuário sem empresa." });

        var relayed = await TryGetLegacyOwnerRelayResultAsync(
            machineId,
            companyId,
            $"api/telemetry/machines/{Uri.EscapeDataString(machineId)}/mdb-status",
            requestBody: null,
            cancellationToken);
        if (relayed is not null) return ToRelayActionResult(relayed);

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
                var statusCode = result.Error == "machine_owner_unknown"
                    ? StatusCodes.Status503ServiceUnavailable
                    : StatusCodes.Status504GatewayTimeout;
                return StatusCode(statusCode, new
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

    [Authorize(Roles = AppRoles.CompanyAdminOrPlatformAdmin)]
    [HttpPost("sessions/{sessionId:guid}/resolve-incomplete-closure")]
    public async Task<IActionResult> ResolveIncompleteClosure(
        Guid sessionId,
        [FromBody] ResolveIncompleteClosureRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var userId, out var companyId)) return BadRequest(new { message = "Usuário sem empresa." });
        if (!request.ConfirmedMachineIdle)
            return BadRequest(new { code = "closure_confirmation_required", message = "Confirme que a máquina foi inspecionada e está ociosa." });

        try
        {
            var session = await _orchestrator.ResolveIncompleteClosureAsync(
                sessionId, companyId, userId, request.EvidenceReference, cancellationToken);
            return Ok(new { sessionId = session.Id, state = session.State });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) when (ex.Message == "closure_evidence_reference_invalid")
        {
            return BadRequest(new { code = ex.Message, message = "Informe uma referência de evidência válida (3 a 64 letras, números ou - _ . / :)." });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { code = ex.Message, message = MapError(ex.Message) });
        }
    }

    [Authorize(Roles = AppRoles.CompanyAdminOrPlatformAdmin)]
    [HttpPost("sessions/{sessionId:guid}/cancel")]
    public async Task<IActionResult> CancelSession(Guid sessionId, [FromBody] CancelSessionRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var userId, out var companyId)) return BadRequest(new { message = "Usuário sem empresa." });
        var sessionForCancellation = await _db.MachineSessions.AsNoTracking()
            .Where(x => x.Id == sessionId && x.CompanyId == companyId)
            .Select(x => new { x.MachineId, x.State, x.ClosedAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (sessionForCancellation is not null &&
            (sessionForCancellation.ClosedAt.HasValue ||
             sessionForCancellation.State == MachineSessionStates.ClosingWithoutSelection))
        {
            return Accepted(new { sessionId, state = sessionForCancellation.State });
        }

        if (sessionForCancellation is not null && _legacyTelemetryRelay?.IsConfigured == true)
        {
            var ownerState = await _db.MachineConnectionStates.AsNoTracking()
                .FirstOrDefaultAsync(state => state.MachineId == sessionForCancellation.MachineId && state.CompanyId == companyId, cancellationToken);
            var closeGuardFreshnessSeconds = Math.Clamp(
                _mdbOptions.StaleAfterSeconds, 10, 3600);
            if (ownerState?.IsOnline == true &&
                (ownerState.CommandRoutingVersion != TelemetryConnectionManager.CurrentCommandRoutingVersion ||
                 ownerState.MdbMenuConnectionId != ownerState.ConnectionId ||
                 ownerState.MdbMenuUpdatedAt == null ||
                 ownerState.MdbMenuUpdatedAt < DateTime.UtcNow.AddSeconds(-closeGuardFreshnessSeconds)))
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new {
                    code = "machine_owner_unknown",
                    message = "O encerramento foi pausado porque a instância proprietária da máquina ainda não confirma o estado MDB antes de fechar a sessão. Aguarde a reconexão compatível." });

            await using var operationTransaction = await _db.Database.BeginTransactionAsync(cancellationToken);
            if (!await MachineOperationLock.AcquireAsync(
                    _db,
                    sessionForCancellation.MachineId,
                    companyId,
                    cancellationToken))
            {
                return NotFound();
            }

            sessionForCancellation = await _db.MachineSessions.AsNoTracking()
                .Where(x => x.Id == sessionId && x.CompanyId == companyId)
                .Select(x => new { x.MachineId, x.State, x.ClosedAt })
                .FirstOrDefaultAsync(CancellationToken.None);
            if (sessionForCancellation is null)
            {
                await operationTransaction.CommitAsync(CancellationToken.None);
                return NotFound();
            }

            if (sessionForCancellation.ClosedAt.HasValue ||
                sessionForCancellation.State == MachineSessionStates.ClosingWithoutSelection)
            {
                await operationTransaction.CommitAsync(CancellationToken.None);
                return Accepted(new { sessionId, state = sessionForCancellation.State });
            }

            var relayed = await TryGetLegacyOwnerRelayResultAsync(
                sessionForCancellation.MachineId,
                companyId,
                $"api/telemetry/sessions/{sessionId:D}/cancel",
                request,
                CancellationToken.None);
            if (relayed is not null)
            {
                await operationTransaction.CommitAsync(CancellationToken.None);
                return ToRelayActionResult(relayed);
            }

            await operationTransaction.CommitAsync(CancellationToken.None);
        }
        try
        {
            var session = await _orchestrator.CancelSessionAsync(sessionId, companyId, userId, request.Reason ?? "user_cancelled", cancellationToken);
            return Accepted(new { sessionId, state = session.State });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) when (ex.Message == "machine_owner_unknown")
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { code = ex.Message, message = MapError(ex.Message) });
        }
        catch (InvalidOperationException ex) { return Conflict(new { code = ex.Message, message = MapError(ex.Message) }); }
    }

    [Authorize(Roles = AppRoles.CompanyAdminOrPlatformAdmin)]
    [HttpPost("machines/{machineId}/commands")]
    public async Task<IActionResult> SendCommand(string machineId, [FromBody] SendCommandRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var userId, out var companyId)) return BadRequest(new { message = "Usuário sem empresa." });
        if (string.IsNullOrWhiteSpace(request.Command)) return BadRequest(new { message = "Comando obrigatório." });
        if (request.RequestId == Guid.Empty) return BadRequest(new { code = "invalid_request_id", message = "Identificador do pedido inválido." });

        var requestId = request.RequestId ?? Guid.NewGuid();
        var priorCommand = await _db.TelemetryCommands.AsNoTracking()
            .FirstOrDefaultAsync(command => command.Id == requestId, cancellationToken);
        if (priorCommand is not null)
        {
            if (priorCommand.CompanyId != companyId || priorCommand.MachineId != machineId ||
                priorCommand.UserId != userId || priorCommand.Command != request.Command)
                return Conflict(new { code = "request_id_conflict", message = "O identificador do pedido já foi usado para outra operação." });

            return await ReplayManualCommandAsync(priorCommand, cancellationToken);
        }

        var machine = await _db.Machines.FirstOrDefaultAsync(x => x.Id == machineId && x.CompanyId == companyId, cancellationToken);
        if (machine == null) return NotFound();
        var monitoring = await _db.MachineConnectionStates.AsNoTracking().FirstOrDefaultAsync(
            x => x.MachineId == machineId && x.CompanyId == companyId,
            cancellationToken);
        if (monitoring?.MonitoringEnabled != true) return Conflict(new { code = "monitoring_inactive", message = "Ative o acompanhamento da máquina." });

        await using var admissionTransaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var admissionGate = await new SessionAdmissionGateService(_db).LockForSessionStartAsync(cancellationToken);
        if (admissionGate is null && _db.Database.IsNpgsql())
        {
            await admissionTransaction.CommitAsync(cancellationToken);
            return AdmissionUnavailable("session_admission_gate_unavailable");
        }
        if (admissionGate?.NewSessionsPaused == true)
        {
            await admissionTransaction.CommitAsync(cancellationToken);
            return AdmissionUnavailable("deployment_draining");
        }

        // Serialize manual sends through the admission gate so concurrent requests with
        // different request IDs cannot both pass the unresolved-command check.
        var duplicateRequest = await _db.TelemetryCommands.AsNoTracking()
            .FirstOrDefaultAsync(command => command.Id == requestId, cancellationToken);
        if (duplicateRequest is not null)
        {
            if (duplicateRequest.CompanyId != companyId || duplicateRequest.MachineId != machineId ||
                duplicateRequest.UserId != userId || duplicateRequest.Command != request.Command)
                return Conflict(new { code = "request_id_conflict", message = "O identificador do pedido já foi usado para outra operação." });

            return await ReplayManualCommandAsync(duplicateRequest, cancellationToken);
        }

        if (await _db.TelemetryCommands.AsNoTracking().AnyAsync(
                command => command.CompanyId == companyId && command.MachineId == machineId &&
                           ((command.Status == "accepted" && command.Error == "command_intent_persisted") ||
                            (command.Status == "failed" &&
                             (command.Error == "ack_timeout" || command.Error == "command_outcome_unknown") &&
                             !_db.TelemetryCommandResolutions.Any(resolution => resolution.TelemetryCommandId == command.Id))),
                cancellationToken))
            return Conflict(new
            {
                code = "command_outcome_unresolved",
                message = "Há um comando anterior sem resultado confirmado para esta máquina. Confira a máquina e resolva a auditoria antes de enviar outro."
            });

        var command = new TelemetryCommand
        {
            Id = requestId,
            CompanyId = companyId,
            MachineId = machineId,
            UserId = userId,
            Command = request.Command,
            DataJson = request.Data == null ? null : System.Text.Json.JsonSerializer.Serialize(request.Data),
            Status = "accepted",
            Error = "command_intent_persisted"
        };
        if (_scopeFactory is null)
        {
            // Preserve compatibility with direct controller construction in isolated callers.
            _db.TelemetryCommands.Add(command);
            await _db.SaveChangesAsync(cancellationToken);
        }
        else
        {
            await PersistCommandAuditIntentAsync(command, cancellationToken);
        }

        await LockCommandAuditIntentAsync(command, cancellationToken);
        if (request.RequestId.HasValue)
        {
            var relayed = await TryGetLegacyOwnerRelayResultAsync(
                machineId,
                companyId,
                $"api/telemetry/machines/{Uri.EscapeDataString(machineId)}/commands",
                request,
                CancellationToken.None);
            if (relayed is not null)
            {
                command.MessageId = ReadRelayMessageId(relayed.Content);
                command.Status = relayed.StatusCode is >= 200 and < 300 ? "acknowledged" : "failed";
                command.Attempts = command.Status == "acknowledged" ? 1 : 0;
                command.SentAt = DateTime.UtcNow;
                command.AckAt = command.Status == "acknowledged" ? DateTime.UtcNow : null;
                command.Error = command.Status == "acknowledged" ? null : MapRelayCommandError(relayed);
                await FinalizeCommandAuditAsync(command);
                using (var relayAuditCommitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                    await admissionTransaction.CommitAsync(relayAuditCommitTimeout.Token);
                return ToRelayActionResult(relayed);
            }
        }
        var result = await _commandSender.SendCommandAsync(machine.SerialNumber, request.Data ?? request.Command, cancellationToken);
        command.MessageId = result.MessageId;
        command.Status = result.Success ? "acknowledged" : "failed";
        command.Attempts = result.Attempts;
        command.SentAt = DateTime.UtcNow;
        command.AckAt = result.Success ? DateTime.UtcNow : null;
        command.Error = result.Error;
        await FinalizeCommandAuditAsync(command);
        using (var auditCommitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            await admissionTransaction.CommitAsync(auditCommitTimeout.Token);
        if (!result.Success) return result.Error == "machine_offline"
            ? Conflict(new { code = result.Error, message = "Máquina offline." })
            : result.Error == "machine_owner_unknown"
                ? StatusCode(StatusCodes.Status503ServiceUnavailable, new { code = result.Error, message = MapError(result.Error) })
            : result.Error == "command_queue_timeout"
                ? StatusCode(StatusCodes.Status503ServiceUnavailable, new { code = result.Error, message = "A máquina está processando outra operação. Nenhum comando foi enviado; aguarde e tente novamente." })
            : StatusCode(504, new
            {
                code = result.Error,
                message = result.Error == "command_outcome_unknown"
                    ? "Não foi possível confirmar o resultado. Verifique o estado atual da máquina e a auditoria do comando antes de tentar novamente."
                    : "A máquina não confirmou o comando."
            });
        return Accepted(new { commandId = command.Id, messageId = result.MessageId, status = command.Status });
    }

    private async Task<IActionResult> ReplayManualCommandAsync(TelemetryCommand command, CancellationToken cancellationToken)
    {
        if (command.Status == "accepted" && command.Error == "command_intent_persisted")
            return Conflict(new { code = "command_in_progress", message = "Este pedido já está em andamento e não será enviado novamente. Aguarde o resultado antes de tentar outra operação." });

        if (command.Status == "acknowledged")
            return Accepted(new { commandId = command.Id, messageId = command.MessageId, status = command.Status });

        if (command.Status == "failed" &&
            (command.Error == "ack_timeout" || command.Error == "command_outcome_unknown"))
        {
            if (await _db.TelemetryCommandResolutions.AsNoTracking()
                    .AnyAsync(resolution => resolution.TelemetryCommandId == command.Id, cancellationToken))
                return Conflict(new { code = "command_request_resolved", message = "Este pedido já foi reconciliado. Confira o estado da máquina antes de iniciar uma nova solicitação." });

            return StatusCode(504, new
            {
                code = command.Error,
                message = "Não foi possível confirmar o resultado. Verifique o estado atual da máquina e a auditoria do comando antes de tentar novamente."
            });
        }

        return command.Error switch
        {
            "machine_offline" => Conflict(new { code = command.Error, message = "Máquina offline." }),
            "monitoring_inactive" => Conflict(new { code = command.Error, message = "Ative o acompanhamento da máquina." }),
            "machine_owner_unknown" => StatusCode(StatusCodes.Status503ServiceUnavailable, new { code = command.Error, message = MapError(command.Error) }),
            "command_queue_timeout" => StatusCode(StatusCodes.Status503ServiceUnavailable, new { code = command.Error, message = "A máquina está processando outra operação. Nenhum comando foi enviado; aguarde e tente novamente." }),
            "deployment_draining" or "session_admission_gate_unavailable" => AdmissionUnavailable(command.Error),
            _ => StatusCode(504, new { code = command.Error ?? "command_outcome_unknown", message = "A máquina não confirmou o comando." })
        };
    }

    private async Task PersistCommandAuditIntentAsync(TelemetryCommand command, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory!.CreateScope();
        var auditDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        auditDb.TelemetryCommands.Add(command);
        await auditDb.SaveChangesAsync(cancellationToken);
    }

    private async Task FinalizeCommandAuditAsync(TelemetryCommand command)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var updated = await _db.TelemetryCommands
            .Where(x => x.Id == command.Id && x.Status == "accepted" && x.Error == "command_intent_persisted")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.MessageId, command.MessageId)
                .SetProperty(x => x.Status, command.Status)
                .SetProperty(x => x.Attempts, command.Attempts)
                .SetProperty(x => x.SentAt, command.SentAt)
                .SetProperty(x => x.AckAt, command.AckAt)
                .SetProperty(x => x.Error, command.Error), timeout.Token);
        if (updated != 1)
            throw new InvalidOperationException("Não foi possível finalizar a auditoria persistida do comando MDB.");
    }

    private async Task LockCommandAuditIntentAsync(TelemetryCommand command, CancellationToken cancellationToken)
    {
        var locked = await _db.TelemetryCommands
            .Where(x => x.Id == command.Id && x.Status == "accepted" && x.Error == "command_intent_persisted")
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, command.Status), cancellationToken);
        if (locked != 1)
            throw new InvalidOperationException("A intenção de auditoria do comando MDB não está mais disponível para envio.");
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
        var items = await query.OrderByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                x.Id,
                x.MachineId,
                x.SessionId,
                x.TransactionId,
                x.EventType,
                x.CreatedAt
            })
            .ToListAsync(cancellationToken);
        return Ok(new { items, page, pageSize, totalItems = total, totalPages = (int)Math.Ceiling(total / (double)pageSize) });
    }

    [HttpPost("socket-ticket")]
    [EnableRateLimiting("telemetry-ticket")]
    public IActionResult CreateSocketTicket()
    {
        if (!TryGetIdentity(out var userId, out var companyId)) return BadRequest(new { message = "Usuário sem empresa." });
        var ticket = _tickets.Issue(userId, companyId);
        return Ok(new { ticket = ticket.Token, expiresAt = ticket.ExpiresAt });
    }

    private async Task<LegacyTelemetryRelayResult?> TryGetLegacyOwnerRelayResultAsync(
        string machineId,
        Guid companyId,
        string relativePath,
        object? requestBody,
        CancellationToken cancellationToken)
    {
        if (_legacyTelemetryRelay?.IsConfigured != true) return null;

        var state = await _db.MachineConnectionStates.AsNoTracking()
            .Where(x => x.MachineId == machineId && x.CompanyId == companyId)
            .Select(x => new
            {
                x.IsOnline,
                x.ConnectionId,
                x.LastSeenAt,
                x.OwnerNodeId,
                x.OwnerConnectionId,
                x.CommandRoutingVersion
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (state?.IsOnline != true ||
            string.IsNullOrWhiteSpace(state.ConnectionId) ||
            state.LastSeenAt < DateTime.UtcNow - MachineConnectionFreshness.Window ||
            !string.IsNullOrWhiteSpace(state.OwnerNodeId) ||
            !string.IsNullOrWhiteSpace(state.OwnerConnectionId) ||
            state.CommandRoutingVersion.HasValue)
        {
            return null;
        }

        return await _legacyTelemetryRelay.TryForwardAsync(
            HttpContext,
            relativePath,
            requestBody,
            cancellationToken);
    }

    private async Task<IActionResult?> ExistingPanelSessionResultAsync(
        string machineId,
        Guid companyId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var session = await _db.MachineSessions.AsNoTracking()
            .Where(x => x.MachineId == machineId && x.CompanyId == companyId &&
                        x.StartedByUserId == userId && x.Source == "panel" && x.ClosedAt == null)
            .OrderByDescending(x => x.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);
        return session is null
            ? null
            : CreatedAtAction(nameof(GetSession), new { sessionId = session.Id }, session);
    }

    private static bool IsRelayErrorCode(LegacyTelemetryRelayResult result, string expectedCode)
    {
        if (result.StatusCode != StatusCodes.Status409Conflict) return false;
        try
        {
            using var document = JsonDocument.Parse(result.Content);
            return document.RootElement.TryGetProperty("code", out var code) &&
                   code.ValueKind == JsonValueKind.String &&
                   string.Equals(code.GetString(), expectedCode, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IActionResult ToRelayActionResult(LegacyTelemetryRelayResult result) => new ContentResult
    {
        StatusCode = result.StatusCode,
        ContentType = result.ContentType ?? "application/json",
        Content = result.Content
    };

    private static int ReadRelayMessageId(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.TryGetProperty("messageId", out var messageId) &&
                   messageId.TryGetInt32(out var parsedMessageId)
                ? parsedMessageId
                : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static string MapRelayCommandError(LegacyTelemetryRelayResult result)
    {
        if (result.StatusCode >= StatusCodes.Status500InternalServerError)
            return "command_outcome_unknown";

        try
        {
            using var document = JsonDocument.Parse(result.Content);
            if (document.RootElement.TryGetProperty("code", out var codeElement) &&
                codeElement.ValueKind == JsonValueKind.String)
            {
                var code = codeElement.GetString();
                if (code is "machine_offline" or "monitoring_inactive" or "deployment_draining" or
                    "session_admission_gate_unavailable" or "command_outcome_unknown")
                    return code;
            }
        }
        catch (JsonException)
        {
        }

        return "legacy_owner_request_failed";
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
        "machine_owner_unknown" => "A máquina continua conectada a uma instância sem roteamento entre nós; nenhum comando foi enviado. Tente novamente após a reconexão compatível.",
        "deployment_draining" => "Novas sessões e comandos manuais estão temporariamente pausados durante uma atualização. Tente novamente em alguns instantes.",
        "session_admission_gate_unavailable" => "Não foi possível confirmar a disponibilidade para abrir uma sessão. Tente novamente em alguns instantes.",
        "closure_not_incomplete" => "Esta sessão não está aguardando confirmação de encerramento.",
        "monitoring_inactive" => "Ative o acompanhamento da máquina.",
        "session_already_active" => "A máquina já possui uma sessão ativa.",
        "mdb_status_unavailable" => "O status MDB ainda não está disponível.",
        "mdb_status_stale" => "O status MDB está desatualizado. Consulte novamente.",
        "mdb_not_ready" => "O MDB não está disponível para abrir uma sessão.",
        "mdb_menu_open" => "Aguarde o fechamento do menu de seleção para abrir uma sessão.",
        "response_timeout" => "A máquina não respondeu à consulta MDB.",
        "invalid_mdb_status" => "A máquina retornou um status MDB desconhecido.",
        "automatic_session_disabled" => "A abertura automática está desativada.",
        "payment_already_approved" => "O pagamento já foi aprovado e não pode ser cancelado desta forma.",
        _ => "Não foi possível concluir a operação."
    };

    private IActionResult AdmissionUnavailable(string code)
    {
        Response.Headers["Retry-After"] = "5";
        return StatusCode(StatusCodes.Status503ServiceUnavailable, new { code, message = MapError(code) });
    }
}

public sealed class MonitoringRequest { public bool Enabled { get; set; } }
public sealed class StartSessionRequest { public string? Source { get; set; } }
public sealed class CancelSessionRequest { public string? Reason { get; set; } }
public sealed class ResolveIncompleteClosureRequest
{
    public bool ConfirmedMachineIdle { get; set; }
    public string? EvidenceReference { get; set; }
}
public sealed class SendCommandRequest
{
    public Guid? RequestId { get; set; }
    public string Command { get; set; } = string.Empty;
    public object? Data { get; set; }
}
