using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;

namespace VendingMachines.Api.Services;

public sealed class SessionOrchestrator : IDeviceTelemetryMessageHandler
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SessionStartGates = new();

    private static readonly string[] TerminalStates =
    {
        MachineSessionStates.Completed,
        MachineSessionStates.ClosedWithoutSelection,
        MachineSessionStates.ClosedByDeviceBeforeSelection,
        MachineSessionStates.ClosureIncomplete,
        MachineSessionStates.Denied,
        MachineSessionStates.Refunded,
        MachineSessionStates.ReconciliationRequired
    };

    private readonly AppDbContext _db;
    private readonly ITelemetryConnectionOperations _connections;
    private readonly TelemetryPanelHub _panelHub;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<SessionOrchestrator> _logger;

    public SessionOrchestrator(
        AppDbContext db,
        ITelemetryConnectionOperations connections,
        TelemetryPanelHub panelHub,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger<SessionOrchestrator> logger)
    {
        _db = db;
        _connections = connections;
        _panelHub = panelHub;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    public async Task<MachineSession> StartSessionAsync(
        string machineId,
        Guid companyId,
        Guid? userId,
        string source,
        CancellationToken cancellationToken)
    {
        var gate = SessionStartGates.GetOrAdd($"{companyId:N}:{machineId}", static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var sessionStartAdmission = _connections.TryBeginSessionStart()
                ?? throw new InvalidOperationException("server_restarting");
            return await StartSessionCoreAsync(machineId, companyId, userId, source, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<MachineSession> StartSessionCoreAsync(
        string machineId,
        Guid companyId,
        Guid? userId,
        string source,
        CancellationToken cancellationToken)
    {
        var machine = await _db.Machines.FirstOrDefaultAsync(
            x => x.Id == machineId && x.CompanyId == companyId, cancellationToken)
            ?? throw new KeyNotFoundException("Máquina não encontrada.");

        await using var admissionTransaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        if (!await MachineOperationLock.AcquireAsync(_db, machineId, companyId, cancellationToken))
            throw new KeyNotFoundException("Máquina não encontrada.");
        await _db.Entry(machine).ReloadAsync(cancellationToken);

        if (!await _connections.IsOnlineAsync(machine.SerialNumber, cancellationToken))
            throw new InvalidOperationException("machine_offline");
        if (!await _connections.HasCommandRouteAsync(machine.SerialNumber, cancellationToken))
            throw new InvalidOperationException("machine_owner_unknown");

        var automatic = string.Equals(source, "automatic_mdb", StringComparison.Ordinal);
        if (automatic && !machine.AutoOpenSessionEnabled)
            throw new InvalidOperationException("automatic_session_disabled");

        var connection = await _db.MachineConnectionStates.AsNoTracking().FirstOrDefaultAsync(
            x => x.MachineId == machineId && x.CompanyId == companyId, cancellationToken);
        var menuFreshnessSeconds = Math.Clamp(_configuration.GetValue<int?>("MdbStatus:StaleAfterSeconds") ?? 90, 10, 3600);
        if (connection?.MdbStatus == MdbStatuses.Inactive && connection.MdbMenuPhase == "inactive" &&
            connection.MdbMenuConnectionId == connection.ConnectionId &&
            connection.MdbMenuUpdatedAt >= DateTime.UtcNow.AddSeconds(-menuFreshnessSeconds))
            throw new InvalidOperationException("mdb_menu_open");
        var enforceMdb = automatic || _configuration.GetValue<bool>("MdbStatus:EnforceOnManualStart");
        if (enforceMdb)
        {
            if (connection?.MonitoringEnabled != true)
                throw new InvalidOperationException("monitoring_inactive");
            if (connection.MdbStatusUpdatedAt == null)
                throw new InvalidOperationException("mdb_status_unavailable");

            var staleAfter = Math.Clamp(
                _configuration.GetValue<int?>("MdbStatus:StaleAfterSeconds") ?? 90,
                10,
                3600);
            if (connection.MdbStatusUpdatedAt < DateTime.UtcNow.AddSeconds(-staleAfter))
                throw new InvalidOperationException("mdb_status_stale");
            if (connection.MdbStatus != MdbStatuses.Enabled &&
                (automatic || connection.MdbStatus != MdbStatuses.Idle))
                throw new InvalidOperationException("mdb_not_ready");
        }

        var existing = await ActiveSessions().FirstOrDefaultAsync(
            x => x.MachineId == machineId && x.CompanyId == companyId,
            cancellationToken);
        if (existing != null) throw new InvalidOperationException("session_already_active");

        var locationNameSnapshot = string.IsNullOrWhiteSpace(machine.ClientName)
            ? machine.Location
            : machine.ClientName;
        if (machine.LocationId is Guid assignedLocationId)
        {
            locationNameSnapshot = await _db.Locations.AsNoTracking()
                .Where(location => location.Id == assignedLocationId && location.CompanyId == companyId)
                .Select(location => location.Name)
                .FirstOrDefaultAsync(cancellationToken) ?? locationNameSnapshot;
        }

        var session = new MachineSession
        {
            MachineId = machineId,
            CompanyId = companyId,
            LocationIdSnapshot = machine.LocationId,
            LocationNameSnapshot = locationNameSnapshot,
            StartedByUserId = userId,
            Source = source,
            State = MachineSessionStates.Opening
        };
        var admissionGate = await new SessionAdmissionGateService(_db).LockForSessionStartAsync(cancellationToken);
        if (admissionGate is null && _db.Database.IsNpgsql())
            throw new InvalidOperationException("session_admission_gate_unavailable");
        if (admissionGate?.NewSessionsPaused == true)
            throw new InvalidOperationException("deployment_draining");

        _db.MachineSessions.Add(session);
        await AddEventAsync(session, "session.opening", "Sessão MDB solicitada.", userId, null, cancellationToken);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            await admissionTransaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsOpenSessionConstraintViolation(ex))
        {
            _db.Entry(session).State = EntityState.Detached;
            foreach (var entry in _db.ChangeTracker.Entries<TelemetryEvent>()
                         .Where(x => x.State == EntityState.Added && x.Entity.SessionId == session.Id))
            {
                entry.State = EntityState.Detached;
            }
            throw new InvalidOperationException("session_already_active", ex);
        }

        await admissionTransaction.DisposeAsync();
        var result = await SendTrackedCommandAsync(machine, session, "ABRIR_SESSAO", userId, cancellationToken);
        if (!result.Success)
        {
            session.State = MachineSessionStates.ClosureIncomplete;
            session.CloseReason = result.Error;
            session.ClosedAt = DateTime.UtcNow;
            await AddEventAsync(session, "command.failed", "Não foi possível abrir a sessão.", userId, null, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException(result.Error ?? "command_failed");
        }

        await TryRecoverOpenSessionAsync(machine, session, cancellationToken);
        await _db.Entry(session).ReloadAsync(cancellationToken);
        await BroadcastSessionAsync(session);
        return session;
    }

    private async Task TryRecoverOpenSessionAsync(
        Machine machine,
        MachineSession session,
        CancellationToken cancellationToken)
    {
        var requestedAt = DateTime.UtcNow;
        var timeout = TimeSpan.FromSeconds(Math.Clamp(
            _configuration.GetValue<int?>("MdbStatus:ResponseTimeoutSeconds") ?? 5, 1, 30));
        DeviceStatusResult status;
        try
        {
            status = await _connections.SendStatusRequestAsync(machine.SerialNumber, timeout, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Não foi possível consultar o status MDB durante a abertura da sessão {SessionId}.", session.Id);
            return;
        }
        if (!status.Success || status.MessageId == 0 || status.ResponseAt < requestedAt ||
            status.ResponseAt == null || status.Status != MdbStatuses.Idle)
            return;

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var updated = await _db.MachineSessions
            .Where(item => item.Id == session.Id && item.CompanyId == session.CompanyId &&
                           item.State == MachineSessionStates.Opening && item.ClosedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.State, MachineSessionStates.AwaitingSelection)
                .SetProperty(item => item.SelectionDeadlineAt, now.AddMinutes(2))
                .SetProperty(item => item.LastEventAt, now), cancellationToken);
        if (updated == 1)
        {
            await AddEventAsync(session, "session.open_already", "Sessão MDB já estava aberta; aguardando seleção.",
                null, status.DataJson, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<MachineSession> ResolveIncompleteClosureAsync(
        Guid sessionId,
        Guid companyId,
        Guid userId,
        string? evidenceReference,
        CancellationToken cancellationToken)
    {
        var reference = evidenceReference?.Trim();
        if (string.IsNullOrEmpty(reference) || reference.Length is < 3 or > 64 ||
            reference.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.' or '/' or ':')))
            throw new InvalidOperationException("closure_evidence_reference_invalid");

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var session = await _db.MachineSessions.FirstOrDefaultAsync(
            item => item.Id == sessionId && item.CompanyId == companyId,
            cancellationToken);
        if (session is null) throw new KeyNotFoundException("Sessão não encontrada.");
        if (session.State != MachineSessionStates.ClosureIncomplete || session.ClosedAt is null)
            throw new InvalidOperationException("closure_not_incomplete");

        var now = DateTime.UtcNow;
        var claimed = await _db.MachineSessions
            .Where(item => item.Id == sessionId && item.CompanyId == companyId &&
                           item.State == MachineSessionStates.ClosureIncomplete && item.ClosedAt != null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.State, MachineSessionStates.ClosureVerified)
                .SetProperty(item => item.LastEventAt, now), cancellationToken);
        if (claimed == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException("closure_not_incomplete");
        }

        session.State = MachineSessionStates.ClosureVerified;
        session.LastEventAt = now;
        await AddEventAsync(
            session,
            "session.closure_verified",
            "Responsável confirmou que a máquina foi inspecionada, está ociosa e pode receber nova sessão.",
            userId,
            JsonSerializer.Serialize(new { evidenceReference = reference }),
            cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await BroadcastSessionAsync(session);
        return session;
    }

    public async Task<MachineSession> CancelSessionAsync(
        Guid sessionId,
        Guid companyId,
        Guid? userId,
        string reason,
        CancellationToken cancellationToken)
    {
        var session = await _db.MachineSessions.Include(x => x.Machine).Include(x => x.Transaction)
            .FirstOrDefaultAsync(x => x.Id == sessionId && x.CompanyId == companyId, cancellationToken)
            ?? throw new KeyNotFoundException("Sessão não encontrada.");

        if (session.ClosedAt.HasValue) return session;
        var machine = session.Machine ?? throw new InvalidOperationException("Máquina da sessão não encontrada.");
        var commandRouteAvailable = await _connections.HasCommandRouteAsync(machine.SerialNumber, cancellationToken);
        var machineOnline = await _connections.IsOnlineAsync(machine.SerialNumber, cancellationToken);
        if (machineOnline && !commandRouteAvailable)
            throw new InvalidOperationException("machine_owner_unknown");
        if ((session.State is MachineSessionStates.Opening or MachineSessionStates.AwaitingSelection) && !commandRouteAvailable)
            throw new InvalidOperationException("machine_offline");

        if (session.State is MachineSessionStates.Opening or MachineSessionStates.AwaitingSelection)
        {
            var statusRequestAt = DateTime.UtcNow;
            var timeoutSeconds = Math.Clamp(_configuration.GetValue<int?>("MdbStatus:ResponseTimeoutSeconds") ?? 5, 1, 30);
            var mdb = await _connections.SendStatusRequestAsync(machine.SerialNumber, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken);
            var staleAfterSeconds = Math.Clamp(_configuration.GetValue<int?>("MdbStatus:StaleAfterSeconds") ?? 90, 10, 3600);
            if (!mdb.Success || mdb.MessageId == 0 || mdb.ResponseAt == null || mdb.ResponseAt < statusRequestAt ||
                mdb.ResponseAt < DateTime.UtcNow.AddSeconds(-staleAfterSeconds) ||
                mdb.Status is not (MdbStatuses.Idle or MdbStatuses.Enabled))
                throw new InvalidOperationException("mdb_status_unavailable");
            if (mdb.Status == MdbStatuses.Enabled)
            {
                var observedStatus = await _db.MachineConnectionStates.FirstOrDefaultAsync(
                    item => item.MachineId == session.MachineId && item.CompanyId == companyId, cancellationToken);
                if (observedStatus != null)
                {
                    observedStatus.MdbStatus = MdbStatuses.Enabled;
                    observedStatus.MdbStatusRaw = mdb.DataJson;
                    observedStatus.MdbStatusUpdatedAt = mdb.ResponseAt;
                    await _db.SaveChangesAsync(cancellationToken);
                }
                if (session.ItemNumber != null || session.TransactionId != null)
                    throw new InvalidOperationException("mdb_reset_session_has_sale");
                session.State = MachineSessionStates.ClosingWithoutSelection;
                session.CloseReason = reason;
                session.SelectionDeadlineAt = null;
                session.LastEventAt = DateTime.UtcNow;
                await AddEventAsync(session, "session.closing_without_selection", reason, userId, mdb.DataJson, cancellationToken);
                await _db.SaveChangesAsync(cancellationToken);
                await CompleteResetClosureAsync(session.Id, companyId, userId, mdb, cancellationToken);
                var recovered = await _db.MachineSessions.AsNoTracking().FirstAsync(item => item.Id == session.Id, cancellationToken);
                await BroadcastSessionAsync(recovered);
                var connectionState = await _db.MachineConnectionStates.FirstOrDefaultAsync(
                    item => item.MachineId == session.MachineId && item.CompanyId == companyId, cancellationToken);
                if (connectionState != null && mdb.ResponseAt.HasValue)
                {
                    var resetMessage = new InboundDeviceMessage(session.MachineId, companyId, machine.SerialNumber,
                        connectionState.ConnectionId ?? string.Empty, mdb.MessageId, mdb.DataJson ?? "{}", mdb.ResponseAt.Value,
                        connectionState.OwnerNodeId);
                    await TryAutoOpenSessionAsync(resetMessage, connectionState, true, session.Id, cancellationToken);
                }
                return recovered;
            }
            session.State = MachineSessionStates.ClosingWithoutSelection;
            session.CloseReason = reason;
            session.SelectionDeadlineAt = null;
            session.LastEventAt = DateTime.UtcNow;
            await AddEventAsync(session, "session.closing_without_selection", reason, userId, null, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            await SendTrackedCommandAsync(machine, session, "FECHAR_SESSAO", userId, cancellationToken);
        }
        else if (session.State == MachineSessionStates.PaymentPending)
        {
            var cancelled = await CancelPendingPaymentAsync(session, reason, userId, cancellationToken);
            if (!cancelled)
                throw new InvalidOperationException("payment_already_approved");
        }
        else
        {
            throw new InvalidOperationException("payment_already_approved");
        }

        await BroadcastSessionAsync(session);
        return session;
    }

    private async Task<bool> CompleteResetClosureAsync(
        Guid sessionId, Guid companyId, Guid? userId, DeviceStatusResult status, CancellationToken cancellationToken)
    {
        await using var transaction = _db.Database.CurrentTransaction == null
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var session = await _db.MachineSessions.Include(item => item.Transaction)
            .FirstOrDefaultAsync(item => item.Id == sessionId && item.CompanyId == companyId, cancellationToken);
        if (session == null || session.ItemNumber != null || session.TransactionId != null || session.Transaction != null ||
            (session.State == MachineSessionStates.ClosingWithoutSelection && session.ClosedAt != null) ||
            (session.State == MachineSessionStates.ClosureIncomplete && session.ClosedAt == null) ||
            session.State is not (MachineSessionStates.ClosingWithoutSelection or MachineSessionStates.ClosureIncomplete))
            return false;
        if (!await MachineOperationLock.AcquireAsync(_db, session.MachineId, companyId, cancellationToken))
            return false;
        var unresolvedLocalCommand = await _db.TelemetryCommands.AsNoTracking().AnyAsync(command =>
            command.SessionId == sessionId && (command.Status == "pending" || command.Status == "accepted" || command.Status == "processing"), cancellationToken);
        var unresolvedRemoteDispatch = await _db.TelemetryCommandDispatches.AsNoTracking().AnyAsync(dispatch =>
            dispatch.MachineId == session.MachineId && dispatch.CompanyId == companyId &&
            dispatch.Operation == "Command" && (dispatch.Status == "Pending" || dispatch.Status == "Processing"), cancellationToken);
        if (unresolvedLocalCommand || unresolvedRemoteDispatch) return false;

        var now = DateTime.UtcNow;
        var updated = await _db.MachineSessions.Where(item => item.Id == sessionId && item.CompanyId == companyId &&
                item.ItemNumber == null && item.TransactionId == null &&
                ((item.State == MachineSessionStates.ClosingWithoutSelection && item.ClosedAt == null) ||
                 (item.State == MachineSessionStates.ClosureIncomplete && item.ClosedAt != null)))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.State, MachineSessionStates.ClosedByDeviceBeforeSelection)
                .SetProperty(item => item.CloseReason, "possible_mdb_reset")
                .SetProperty(item => item.ClosedAt, now)
                .SetProperty(item => item.SelectionDeadlineAt, (DateTime?)null)
                .SetProperty(item => item.LastEventAt, now), cancellationToken);
        if (updated == 0) return false;
        await _db.Entry(session).ReloadAsync(cancellationToken);
        await AddEventAsync(session, "session.mdb_reset_suspected",
            "MDB respondeu enabled_state enquanto o sistema aguardava o fechamento; sessão concluída sem FECHAR_SESSAO.",
            userId, status.DataJson, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        if (transaction != null) await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task HandleDeviceMessageAsync(InboundDeviceMessage message, CancellationToken cancellationToken)
    {
        JsonElement data;
        try { data = JsonSerializer.Deserialize<JsonElement>(message.DataJson); }
        catch (JsonException) { return; }
        if (data.ValueKind != JsonValueKind.Object) return;

        var command = GetString(data, "command");
        if (string.Equals(command, "status", StringComparison.OrdinalIgnoreCase))
        {
            await HandleMdbStatusAsync(message, data, cancellationToken);
            return;
        }

        var type = GetString(data, "type");
        if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(type)) return;

        MachineSession? session = null;
        List<MachineSession> recentClosedDeliverySessions = [];
        var deliverySuccessWithoutTransaction = command == "vend" && type == "success" && IsMissingTransactionId(data);
        if (command == "vend" && type == "success")
        {
            if (deliverySuccessWithoutTransaction)
                recentClosedDeliverySessions = await FindRecentClosedDeliverySessionsAsync(message, cancellationToken);
            else
                session = await FindClosedDeliverySessionByTransactionAsync(message, data, cancellationToken);
        }

        session ??= await ActiveSessions().Include(x => x.Machine).Include(x => x.Transaction)
            .FirstOrDefaultAsync(x => x.MachineId == message.MachineId && x.CompanyId == message.CompanyId, cancellationToken);

        if (deliverySuccessWithoutTransaction && recentClosedDeliverySessions.Count > 0)
        {
            if (session != null || recentClosedDeliverySessions.Count > 1)
            {
                await AddStandaloneEventAsync(
                    message,
                    "delivery.ambiguous_session_correlation",
                    "Success recebido sem transactionId enquanto há sessão ativa ou mais de uma sessão recente encerrada com entrega registrada; nenhum estado de sessão foi alterado.",
                    message.DataJson,
                    cancellationToken);
                return;
            }

            session = recentClosedDeliverySessions[0];
        }
        if (session == null)
        {
            await AddStandaloneEventAsync(message, $"{command}.{type}", "Evento recebido sem sessão ativa.", message.DataJson, cancellationToken);
            return;
        }

        session.LastEventAt = message.ReceivedAt;
        var deliverySuccessAlreadyResolved = command == "vend" && type == "success" &&
            session.State is MachineSessionStates.Completed or MachineSessionStates.DeliveryFailed or
                MachineSessionStates.RefundPending or MachineSessionStates.Refunded or
                MachineSessionStates.ReconciliationRequired;
        if (!deliverySuccessAlreadyResolved)
            await AddEventAsync(session, $"{command}.{type}", null, null, message.DataJson, cancellationToken);

        var transactionCorrelation = GetTransactionCorrelation(session, data);
        if (transactionCorrelation is TransactionCorrelation.Invalid or TransactionCorrelation.Mismatch)
        {
            var eventType = transactionCorrelation == TransactionCorrelation.Invalid
                ? "protocol.invalid_transaction_id"
                : "protocol.transaction_mismatch";
            var detail = transactionCorrelation == TransactionCorrelation.Invalid
                ? "O transactionId informado pela máquina é inválido."
                : "O transactionId informado não pertence à sessão ativa da máquina.";
            await AddEventAsync(session, eventType, detail, null, message.DataJson, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            await BroadcastSessionAsync(session);
            return;
        }

        if (command == "pool" && type == "begin_session")
        {
            if (session.State == MachineSessionStates.Opening)
            {
                var deadline = DateTime.UtcNow.AddMinutes(2);
                await _db.MachineSessions
                    .Where(item => item.Id == session.Id && item.State == MachineSessionStates.Opening && item.ClosedAt == null)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.State, MachineSessionStates.AwaitingSelection)
                        .SetProperty(item => item.SelectionDeadlineAt, deadline), cancellationToken);
                await _db.Entry(session).ReloadAsync(cancellationToken);
            }
        }
        else if (command == "vend" && type == "request")
        {
            // The status query can still be in flight when the device sends its selection.
            // Let the durable inbox retry rather than discard that selection prematurely.
            if (session.State == MachineSessionStates.Opening &&
                session.StartedAt > DateTime.UtcNow.AddSeconds(-45))
                throw new InvalidOperationException("session_opening_in_progress");

            if (session.State != MachineSessionStates.AwaitingSelection)
            {
                await AddEventAsync(session, "protocol.invalid_vend_request", "Solicitação recebida fora de AwaitingSelection.", null, message.DataJson, cancellationToken);
            }
            else
            {
                var price = GetInt(data, "itemPrice");
                var item = GetInt(data, "itemNumber");
                if (price <= 0 || item < 0)
                {
                    await DenyWithoutPaymentAsync(session, "invalid_vend_request", cancellationToken);
                }
                else
                {
                    var selectedSlot = await _db.MachineProductSlots.AsNoTracking()
                        .Where(slot => slot.CompanyId == session.CompanyId &&
                                       slot.MachineId == session.MachineId &&
                                       slot.ItemNumber == item)
                        .Select(slot => new
                        {
                            slot.ProductId,
                            ProductName = _db.Products
                                .Where(product => product.Id == slot.ProductId &&
                                                  (product.CompanyId == null || product.CompanyId == session.CompanyId))
                                .Select(product => product.Name)
                                .FirstOrDefault()
                        })
                        .FirstOrDefaultAsync(cancellationToken);
                    // Serialize the selection snapshot with menu closure. A retry can
                    // continue from a snapshot persisted before a payment was created.
                    await using var selectionTransaction = await _db.Database.BeginTransactionAsync(cancellationToken);
                    if (!await MachineOperationLock.AcquireAsync(_db, session.MachineId, session.CompanyId, cancellationToken))
                        throw new InvalidOperationException("machine_not_found");
                    await _db.Entry(session).ReloadAsync(cancellationToken);
                    var canSelect = session.State == MachineSessionStates.AwaitingSelection && session.ClosedAt == null &&
                        (session.ItemNumber == null || (session.ItemNumber == item && session.AmountCents == price));
                    if (canSelect)
                    {
                        session.ItemNumber = item;
                        session.AmountCents = price;
                        session.SelectedProductIdSnapshot = selectedSlot?.ProductId;
                        session.SelectedProductNameSnapshot = selectedSlot?.ProductName;
                        session.SelectionDeadlineAt = null;
                        await _db.SaveChangesAsync(cancellationToken);
                    }
                    await selectionTransaction.CommitAsync(cancellationToken);
                    await selectionTransaction.DisposeAsync();
                    if (canSelect)
                        await CreateAndSendPixAsync(session, price, item, cancellationToken);
                }
            }
        }
        else if (command == "pool" && type == "vend_approved")
        {
            if (session.State == MachineSessionStates.PaymentApproved)
                session.State = MachineSessionStates.AwaitingDeliveryResult;
        }
        else if (command == "pool" && type == "vend_denied")
        {
            if (session.State == MachineSessionStates.DeniedAwaitingClosure)
                session.CloseReason ??= "payment_rejected";
        }
        else if (command == "vend" && type == "success")
        {
            await HandleDeliverySuccessAsync(session, data, cancellationToken);
        }
        else if (command == "vend" && type == "failure")
        {
            await HandleDeliveryFailureAsync(session, data, message.DataJson, cancellationToken);
        }
        else if (command == "vend" && type == "sessioncomplete")
        {
            if (session.State == MachineSessionStates.AwaitingSelection)
            {
                session.State = MachineSessionStates.DeviceClosingBeforeSelection;
                session.SelectionDeadlineAt = null;
                session.CloseReason = "device_closed_before_selection";
            }
        }
        else if (command == "pool" && type == "end_session")
        {
            await HandleEndSessionAsync(session, data, cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
        await BroadcastSessionAsync(session);
    }

    private async Task<MachineSession?> FindClosedDeliverySessionByTransactionAsync(
        InboundDeviceMessage message,
        JsonElement data,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(GetString(data, "transactionId"), out var transactionId))
            return null;

        var matches = await _db.MachineSessions.Include(x => x.Transaction)
            .Where(x => x.MachineId == message.MachineId &&
                        x.CompanyId == message.CompanyId &&
                        x.ClosedAt != null &&
                        x.TransactionId == transactionId &&
                        x.ItemNumber != null)
            .Take(2)
            .ToListAsync(cancellationToken);

        return matches.Count == 1 ? matches[0] : null;
    }

    private async Task<List<MachineSession>> FindRecentClosedDeliverySessionsAsync(
        InboundDeviceMessage message,
        CancellationToken cancellationToken)
    {
        var lowerBound = message.ReceivedAt.AddMinutes(-2);
        return await _db.MachineSessions.Include(x => x.Transaction)
            .Where(x => x.MachineId == message.MachineId &&
                        x.CompanyId == message.CompanyId &&
                        x.ClosedAt != null && x.ClosedAt >= lowerBound && x.ClosedAt <= message.ReceivedAt &&
                        x.TransactionId != null &&
                        x.ItemNumber != null)
            .OrderByDescending(x => x.ClosedAt)
            .Take(2)
            .ToListAsync(cancellationToken);
    }

    public async Task HandlePaymentResultAsync(Guid transactionId, bool approved, string reason, CancellationToken cancellationToken)
    {
        var session = await _db.MachineSessions.Include(x => x.Machine).Include(x => x.Transaction)
            .FirstOrDefaultAsync(x => x.TransactionId == transactionId &&
                                      x.Transaction != null && x.Machine != null &&
                                      x.CompanyId == x.Transaction.CompanyId &&
                                      x.MachineId == x.Transaction.MachineId &&
                                      x.CompanyId == x.Machine.CompanyId,
                cancellationToken);
        if (session == null || session.Transaction == null || session.Machine == null) return;

        var claimedPendingResult = false;
        if (session.State == MachineSessionStates.PaymentPending)
        {
            var now = DateTime.UtcNow;
            var deliveryDeadline = approved ? now.AddSeconds(60) : (DateTime?)null;
            var claimed = await _db.MachineSessions
                .Where(item => item.Id == session.Id && item.State == MachineSessionStates.PaymentPending)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.State, approved
                        ? MachineSessionStates.PaymentApproved
                        : MachineSessionStates.DeniedAwaitingClosure)
                    .SetProperty(item => item.PaymentDeadlineAt, (DateTime?)null)
                    .SetProperty(item => item.DeliveryDeadlineAt, deliveryDeadline), cancellationToken);

            if (claimed > 0)
            {
                claimedPendingResult = true;
                session.State = approved
                    ? MachineSessionStates.PaymentApproved
                    : MachineSessionStates.DeniedAwaitingClosure;
                session.PaymentDeadlineAt = null;
                session.DeliveryDeadlineAt = deliveryDeadline;
            }
            else
            {
                await _db.Entry(session).ReloadAsync(cancellationToken);
                await _db.Entry(session.Transaction).ReloadAsync(cancellationToken);
            }
        }

        if (!claimedPendingResult && session.State != MachineSessionStates.PaymentPending)
        {
            if (approved && session.State is MachineSessionStates.DeniedAwaitingClosure or MachineSessionStates.Denied)
            {
                session.Transaction.Status = "Approved";
                await AddEventAsync(session, "payment.approved_after_cancellation", reason, null, null, cancellationToken);
                await _db.SaveChangesAsync(cancellationToken);
                await BeginRefundAsync(session, "approved_after_cancellation", cancellationToken);
                return;
            }
            await AddEventAsync(session, "payment.duplicate_result", reason, null, null, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        if (!claimedPendingResult)
        {
            throw new InvalidOperationException("Não foi possível reivindicar o resultado do pagamento para a sessão.");
        }

        if (approved)
        {
            session.Transaction.Status = "Approved";
            session.Transaction.CompletedAt ??= DateTime.UtcNow;
            await AddEventAsync(session, "payment.approved", reason, null, null, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            var result = await SendTrackedCommandAsync(session.Machine, session, "VENDA_APROVADA", null, cancellationToken);
            if (!result.Success) await BeginRefundAsync(session, "approval_delivery_failed", cancellationToken);
        }
        else
        {
            session.Transaction.Status = "Rejected";
            session.Transaction.CompletedAt = DateTime.UtcNow;
            session.CloseReason = reason;
            await AddEventAsync(session, "payment.denied", reason, null, null, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            await SendTrackedCommandAsync(session.Machine, session, "VENDA_NEGADA", null, cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
        await BroadcastSessionAsync(session);
    }

    public async Task ProcessDeadlinesAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var connectionFreshnessCutoff = now - MachineConnectionFreshness.Window;
        var sessions = await ActiveSessions().Include(x => x.Machine).Include(x => x.Transaction)
            .Where(session => !_db.MachineConnectionStates.AsNoTracking().Any(state =>
                            state.MachineId == session.MachineId &&
                            state.IsOnline && state.LastSeenAt >= connectionFreshnessCutoff &&
                            (state.OwnerNodeId == null || state.OwnerNodeId == "" ||
                             state.ConnectionId == null || state.ConnectionId == "" ||
                             state.OwnerConnectionId != state.ConnectionId ||
                             state.CommandRoutingVersion != TelemetryConnectionManager.CurrentCommandRoutingVersion ||
                             state.OwnerLeaseExpiresAt == null || state.OwnerLeaseExpiresAt <= now)))
                        .Where(session => (session.SelectionDeadlineAt != null && session.SelectionDeadlineAt <= now) ||
                                         (session.PaymentDeadlineAt != null && session.PaymentDeadlineAt <= now) ||
                                         (session.DeliveryDeadlineAt != null && session.DeliveryDeadlineAt <= now) ||
                                         ((session.State == MachineSessionStates.ClosingWithoutSelection ||
                                           session.State == MachineSessionStates.DeviceClosingBeforeSelection ||
                                           session.State == MachineSessionStates.DeniedAwaitingClosure ||
                                           session.State == MachineSessionStates.DeliveryFailed ||
                                           session.State == MachineSessionStates.RefundPending ||
                                           session.State == MachineSessionStates.Refunded ||
                                           session.State == MachineSessionStates.ReconciliationRequired ||
                                           session.State == MachineSessionStates.Completed) && session.LastEventAt <= now.AddSeconds(-30)))
            .ToListAsync(cancellationToken);

        foreach (var session in sessions)
        {
            if (session.SelectionDeadlineAt <= now && session.State == MachineSessionStates.AwaitingSelection)
            {
                try
                {
                    await CancelSessionAsync(session.Id, session.CompanyId, null, "selection_timeout", cancellationToken);
                }
                catch (InvalidOperationException ex) when (ex.Message == "mdb_status_unavailable")
                {
                    if (!await _db.TelemetryEvents.AnyAsync(item => item.SessionId == session.Id &&
                            item.EventType == "session.close_deferred", cancellationToken))
                    {
                        await AddEventAsync(session, "session.close_deferred",
                            "Fechamento pendente: confirme o status MDB antes de enviar FECHAR_SESSAO.", null, null, cancellationToken);
                        await _db.SaveChangesAsync(cancellationToken);
                        await BroadcastSessionAsync(session);
                    }
                }
            }
            else if (session.PaymentDeadlineAt <= now && session.State == MachineSessionStates.PaymentPending)
            {
                await CancelPendingPaymentAsync(session, "payment_timeout", null, cancellationToken);
            }
            else if (session.DeliveryDeadlineAt <= now && session.State is MachineSessionStates.PaymentApproved or MachineSessionStates.AwaitingDeliveryResult)
            {
                await BeginDeliveryTimeoutRefundAsync(session, now, cancellationToken);
            }
            else if (session.LastEventAt <= now.AddSeconds(-30))
            {
                await using var closureTransaction = await _db.Database.BeginTransactionAsync(cancellationToken);
                var closeReason = session.CloseReason ?? "missing_end_session";
                var nextState = session.State is MachineSessionStates.ClosingWithoutSelection or
                    MachineSessionStates.DeviceClosingBeforeSelection or MachineSessionStates.DeniedAwaitingClosure
                    ? MachineSessionStates.ClosureIncomplete
                    : session.State;
                var closed = await _db.MachineSessions
                    .Where(item => item.Id == session.Id && item.CompanyId == session.CompanyId &&
                                   item.ClosedAt == null && item.State == session.State &&
                                   item.LastEventAt <= now.AddSeconds(-30))
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.ClosedAt, now)
                        .SetProperty(item => item.CloseReason, closeReason)
                        .SetProperty(item => item.State, nextState)
                        .SetProperty(item => item.LastEventAt, now), cancellationToken);
                if (closed == 1)
                {
                    await _db.Entry(session).ReloadAsync(cancellationToken);
                    await AddEventAsync(session, "session.closure_incomplete", session.CloseReason, null, null, cancellationToken);
                    await _db.SaveChangesAsync(cancellationToken);
                    await closureTransaction.CommitAsync(cancellationToken);
                    await BroadcastSessionAsync(session);
                }
                else
                {
                    await closureTransaction.RollbackAsync(cancellationToken);
                    await _db.Entry(session).ReloadAsync(cancellationToken);
                }
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task BeginDeliveryTimeoutRefundAsync(
        MachineSession session,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (session.Transaction == null || !session.TransactionId.HasValue)
            return;

        await using var transactionScope = await _db.Database.BeginTransactionAsync(cancellationToken);
        var claimed = await _db.MachineSessions
            .Where(item => item.Id == session.Id &&
                item.TransactionId != null &&
                item.DeliveryDeadlineAt != null && item.DeliveryDeadlineAt <= now &&
                (item.State == MachineSessionStates.PaymentApproved ||
                 item.State == MachineSessionStates.AwaitingDeliveryResult))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.State, MachineSessionStates.RefundPending)
                .SetProperty(item => item.DeliveryDeadlineAt, (DateTime?)null), cancellationToken);

        if (claimed == 0)
        {
            await transactionScope.RollbackAsync(cancellationToken);
            await _db.Entry(session).ReloadAsync(cancellationToken);
            return;
        }

        session.State = MachineSessionStates.RefundPending;
        session.DeliveryDeadlineAt = null;
        var refundQueued = await QueueRefundAsync(session, "delivery_timeout", cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await transactionScope.CommitAsync(cancellationToken);

        if (refundQueued)
            await ProcessOutboxAsync(cancellationToken);
        await BroadcastSessionAsync(session);
    }

    public async Task ProcessOutboxAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var legacyOverlapActive = !string.IsNullOrWhiteSpace(_configuration["Telemetry:LegacyApiBaseUrl"]);
        var claimableStatus = legacyOverlapActive ? "deferred_candidate_overlap" : "pending";
        var sessionsToBroadcast = new List<MachineSession>();
        var refundedTransactionIds = new HashSet<Guid>();

        // The previous API consumes only Pending rows and cannot claim them atomically.
        // During overlap, process only candidate-owned deferred rows; the old binary
        // ignores that status, so it cannot race the candidate for these provider calls.
        if (!legacyOverlapActive)
        {
            await _db.OutboxMessages
                .Where(message => message.Status == "deferred_candidate_overlap" ||
                                  message.Status == "deferred_legacy_overlap")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(message => message.Status, "pending")
                    .SetProperty(message => message.NextAttemptAt, now)
                    .SetProperty(message => message.LastError, (string?)null), cancellationToken);
        }

        await _db.OutboxMessages
            .Where(x => x.Status == "processing" && (x.NextAttemptAt == null || x.NextAttemptAt <= now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, claimableStatus)
                .SetProperty(x => x.NextAttemptAt, now)
                .SetProperty(x => x.LastError, "worker_lease_expired"), cancellationToken);

        var messages = await _db.OutboxMessages
            .Where(x => x.Status == claimableStatus && (x.NextAttemptAt == null || x.NextAttemptAt <= now))
            .OrderBy(x => x.CreatedAt)
            .Take(25)
            .ToListAsync(cancellationToken);

        foreach (var message in messages)
        {
            var claimTime = DateTime.UtcNow;
            var leaseExpiresAt = claimTime.AddMinutes(2);
            int claimed;
            try
            {
                claimed = await _db.OutboxMessages
                    .Where(x => x.Id == message.Id &&
                                x.Status == claimableStatus &&
                                (x.NextAttemptAt == null || x.NextAttemptAt <= claimTime))
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.Status, "processing")
                        .SetProperty(x => x.NextAttemptAt, leaseExpiresAt)
                        .SetProperty(x => x.LastError, (string?)null), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await ReleaseOutboxClaimAfterShutdownAsync(message.Id, leaseExpiresAt);
                throw;
            }
            if (claimed == 0) continue;

            try
            {
                // ExecuteUpdate bypasses EF's tracked snapshot. Reload the successful claim so a later
                // failure transition from Processing back to Pending is detected and persisted.
                await _db.Entry(message).ReloadAsync(cancellationToken);

                if (message.MessageType is not ("refund_payment" or "cancel_payment"))
                {
                    message.Status = "failed";
                    message.LastError = "unsupported_message_type";
                    message.ProcessedAt = DateTime.UtcNow;
                    _logger.LogError(
                        "Outbox financeiro {OutboxId} foi encerrado por tipo de mensagem não suportado.",
                        message.Id);
                    await _db.SaveChangesAsync(cancellationToken);
                    continue;
                }

                var transaction = message.TransactionId.HasValue
                    ? await _db.PaymentTransactions.FirstOrDefaultAsync(x => x.Id == message.TransactionId, cancellationToken)
                    : null;
                var session = message.SessionId.HasValue
                    ? await _db.MachineSessions.FirstOrDefaultAsync(x => x.Id == message.SessionId, cancellationToken)
                    : null;
                if (transaction == null)
                {
                    message.Status = "failed";
                    message.LastError = "transaction_not_found";
                    message.ProcessedAt = now;
                    await _db.SaveChangesAsync(cancellationToken);
                    continue;
                }

                var associationIsValid = transaction.CompanyId == message.CompanyId &&
                                         transaction.MachineId == message.MachineId &&
                                         (!message.SessionId.HasValue ||
                                          session != null &&
                                          session.CompanyId == message.CompanyId &&
                                          session.MachineId == message.MachineId &&
                                          session.TransactionId == transaction.Id);
                if (!associationIsValid)
                {
                    message.Status = "failed";
                    message.LastError = "association_mismatch";
                    message.ProcessedAt = now;
                    _logger.LogError(
                        "Outbox financeiro {OutboxId} foi encerrado porque sessão, cobrança e máquina não correspondem.",
                        message.Id);
                    await _db.SaveChangesAsync(cancellationToken);
                    continue;
                }

                var success = message.MessageType switch
                {
                    "refund_payment" => await RefundProviderPaymentAsync(transaction, cancellationToken),
                    "cancel_payment" => await CancelProviderPaymentAsync(transaction, cancellationToken),
                    _ => false
                };

                var processedAt = DateTime.UtcNow;
                message.Attempts++;
                if (success)
                {
                    message.Status = "processed";
                    message.ProcessedAt = processedAt;
                    if (message.MessageType == "refund_payment" && session != null)
                    {
                        session.State = MachineSessionStates.Refunded;
                        transaction.Status = "Refunded";
                        refundedTransactionIds.Add(transaction.Id);
                        var sale = await _db.SaleRecords.FirstOrDefaultAsync(
                            record => record.TransactionId == transaction.Id &&
                                      record.CompanyId == transaction.CompanyId &&
                                      record.MachineId == transaction.MachineId,
                            cancellationToken);
                        if (sale != null)
                        {
                            sale.RefundedAmount = sale.GrossAmount;
                            sale.FinancialStatus = "Refunded";
                        }
                        await AddEventAsync(session, "refund.completed", null, null, null, cancellationToken);
                        sessionsToBroadcast.Add(session);
                    }
                    else if (message.MessageType == "cancel_payment")
                    {
                        transaction.Status = "Cancelled";
                        transaction.CompletedAt = processedAt;
                    }
                }
                else if (message.Attempts >= 5)
                {
                    message.Status = "failed";
                    message.LastError = "provider_operation_failed";
                    message.ProcessedAt = processedAt;
                    if (message.MessageType == "refund_payment" && session != null)
                    {
                        session.State = MachineSessionStates.ReconciliationRequired;
                        transaction.Status = "ReconciliationRequired";
                        await AddEventAsync(session, "refund.failed", "Estorno falhou após cinco tentativas.", null, null, cancellationToken);
                        sessionsToBroadcast.Add(session);
                    }
                    else if (message.MessageType == "cancel_payment")
                    {
                        transaction.Status = "ReconciliationRequired";
                        if (session != null)
                        {
                            session.State = MachineSessionStates.ReconciliationRequired;
                            await AddEventAsync(
                                session,
                                "payment.cancellation_failed",
                                "Cancelamento financeiro falhou após cinco tentativas.",
                                null,
                                null,
                                cancellationToken);
                            sessionsToBroadcast.Add(session);
                        }
                    }
                }
                else
                {
                    message.Status = claimableStatus;
                    message.LastError = "provider_operation_failed";
                    message.NextAttemptAt = DateTime.UtcNow.AddSeconds(Math.Min(300, 5 * Math.Pow(2, message.Attempts)));
                }

                // Persist this item's terminal/retry state before spending time on the next
                // provider request, so a batch cannot outlive earlier items' two-minute leases.
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await ReleaseOutboxClaimAfterShutdownAsync(message.Id, leaseExpiresAt);
                throw;
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        foreach (var transactionId in refundedTransactionIds)
            await SynchronizeRefundedSaleAsync(transactionId, cancellationToken);
        foreach (var session in sessionsToBroadcast.DistinctBy(x => x.Id))
            await BroadcastSessionAsync(session);
    }

    private async Task ReleaseOutboxClaimAfterShutdownAsync(Guid outboxId, DateTime leaseExpiresAt)
    {
        try
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var retryAt = DateTime.UtcNow;
            var retryStatus = string.IsNullOrWhiteSpace(_configuration["Telemetry:LegacyApiBaseUrl"])
                ? "pending"
                : "deferred_candidate_overlap";
            var released = await _db.OutboxMessages
                .Where(message => message.Id == outboxId &&
                    message.Status == "processing" && message.NextAttemptAt == leaseExpiresAt)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(message => message.Status, retryStatus)
                    .SetProperty(message => message.NextAttemptAt, (DateTime?)retryAt)
                    .SetProperty(message => message.LastError, (string?)null), cleanupTimeout.Token);

            if (released > 0)
                _logger.LogInformation("Outbox financeiro {OutboxId} liberado para retomada após cancelamento do worker.", outboxId);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Não foi possível liberar o lease do outbox financeiro {OutboxId} durante o encerramento; ele será retomado após expiração do lease. Tipo de erro: {ExceptionType}.",
                outboxId,
                exception.GetType().Name);
        }
    }

    private async Task CreateAndSendPixAsync(MachineSession session, int amountCents, int itemNumber, CancellationToken cancellationToken)
    {
        var machine = session.Machine ?? await _db.Machines.FirstAsync(x => x.Id == session.MachineId, cancellationToken);
        if (!machine.MercadoPagoEnabled)
        {
            await DenyWithoutPaymentAsync(session, "mercado_pago_disabled", cancellationToken);
            return;
        }

        var existing = session.TransactionId.HasValue
            ? await _db.PaymentTransactions.FirstOrDefaultAsync(x => x.Id == session.TransactionId.Value, cancellationToken)
            : null;
        if (existing != null &&
            (existing.Status != "Pending" || !string.IsNullOrWhiteSpace(existing.MercadoPagoPaymentId)))
        {
            await SendPixPayloadAsync(machine, session, existing, cancellationToken);
            return;
        }

        PaymentTransaction transaction;
        decimal amount;
        decimal fee;
        if (existing != null)
        {
            transaction = existing;
            amount = transaction.Amount;
            fee = transaction.ApplicationFee;
        }
        else
        {
            amount = amountCents / 100m;
            var feePercent = await PlatformFeeSettingsReader.ReadValidFeePercentAsync(_db, cancellationToken);
            if (!feePercent.HasValue)
            {
                await DenyWithoutPaymentAsync(session, "invalid_application_fee", cancellationToken);
                return;
            }

            fee = Math.Round(amount * feePercent.Value / 100m, 2);
            if (fee <= 0m || fee >= amount)
            {
                await DenyWithoutPaymentAsync(session, "invalid_application_fee", cancellationToken);
                return;
            }

            transaction = new PaymentTransaction
            {
                MachineId = machine.Id,
                CompanyId = session.CompanyId,
                Amount = amount,
                ApplicationFee = fee,
                Status = "Pending",
                SendTelemetryToMachine = true
            };
            _db.PaymentTransactions.Add(transaction);
            session.TransactionId = transaction.Id;
            session.State = MachineSessionStates.PaymentPending;
            session.PaymentDeadlineAt = DateTime.UtcNow.AddMinutes(5);
            await _db.SaveChangesAsync(cancellationToken);
        }

        try
        {
            if (_environment.IsDevelopment())
            {
                transaction.MercadoPagoPaymentId = $"mock_mp_{transaction.Id:N}";
                transaction.MercadoPagoStatus = "pending";
                transaction.MercadoPagoStatusDetail = "pending_waiting_transfer";
                transaction.QrCode = $"000201-vendmachine-{transaction.Id:N}";
            }
            else
            {
                var integration = await _db.MercadoPagoIntegrations.FirstOrDefaultAsync(
                    x => x.CompanyId == session.CompanyId && x.IsActive, cancellationToken)
                    ?? throw new InvalidOperationException("Integração Mercado Pago não configurada.");
                var accessToken = EncryptionService.Decrypt(integration.AccessToken);
                if (accessToken.Contains("mock", StringComparison.OrdinalIgnoreCase) || accessToken.Length < 15)
                    throw new InvalidOperationException("Credenciais Mercado Pago de simulação ou inválidas não podem ser usadas fora do ambiente de desenvolvimento.");

                var client = _httpClientFactory.CreateClient("MercadoPago");
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                client.DefaultRequestHeaders.Add("X-Idempotency-Key", transaction.Id.ToString());
                var payload = new
                {
                    transaction_amount = amount,
                    description = $"Venda item {itemNumber} - {machine.Name}",
                    payment_method_id = "pix",
                    payer = new { email = "comprador@vendmachine.com.br", first_name = "Cliente", last_name = "Vending" },
                    application_fee = fee,
                    notification_url = _configuration["MercadoPago:WebhookUrl"],
                    external_reference = transaction.Id.ToString()
                };
                var response = await client.PostAsJsonAsync("https://api.mercadopago.com/v1/payments", payload, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                transaction.MercadoPagoPaymentId = root.GetProperty("id").GetRawText();
                transaction.MercadoPagoStatus = GetString(root, "status");
                transaction.MercadoPagoStatusDetail = GetString(root, "status_detail");
                var txData = root.GetProperty("point_of_interaction").GetProperty("transaction_data");
                transaction.QrCode = GetString(txData, "qr_code") ?? string.Empty;
                transaction.QrCodeBase64 = GetString(txData, "qr_code_base64") ?? string.Empty;
            }

            await AddEventAsync(session, "payment.pix_created", $"Pix criado para o item {itemNumber}.", null, null, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            await SendPixPayloadAsync(machine, session, transaction, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Keep the durable pending transaction so the inbox retry can resume with the same idempotency key.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao criar Pix para a sessão {SessionId}.", session.Id);
            transaction.Status = "Failed";
            await DenyWithoutPaymentAsync(session, "provider_error", cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
        await BroadcastSessionAsync(session);
    }

    private async Task SendPixPayloadAsync(Machine machine, MachineSession session, PaymentTransaction transaction, CancellationToken cancellationToken)
    {
        var payload = new
        {
            command = "pay",
            type = "pix",
            code = transaction.QrCode,
            transactionId = transaction.Id,
            amount = session.AmountCents,
            expiresAt = session.PaymentDeadlineAt
        };
        var result = await SendTrackedCommandAsync(machine, session, payload, null, cancellationToken);
        if (!result.Success) await CancelPendingPaymentAsync(session, "machine_offline", null, cancellationToken);
    }

    private async Task HandleDeliverySuccessAsync(MachineSession session, JsonElement data, CancellationToken cancellationToken)
    {
        if (session.State is MachineSessionStates.PaymentApproved or MachineSessionStates.AwaitingDeliveryResult)
        {
            await using var transactionScope = await _db.Database.BeginTransactionAsync(cancellationToken);
            var claimed = await _db.MachineSessions
                .Where(item => item.Id == session.Id &&
                    (item.State == MachineSessionStates.PaymentApproved ||
                     item.State == MachineSessionStates.AwaitingDeliveryResult))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.State, MachineSessionStates.Completed)
                    .SetProperty(item => item.DeliveryDeadlineAt, (DateTime?)null), cancellationToken);

            if (claimed == 0)
            {
                await transactionScope.RollbackAsync(cancellationToken);
                await _db.Entry(session).ReloadAsync(cancellationToken);
                if (session.Transaction != null)
                    await _db.Entry(session.Transaction).ReloadAsync(cancellationToken);
                if (session.State == MachineSessionStates.Completed)
                    await AddEventAsync(session, "delivery.duplicate_result", "Success duplicado recebido pela máquina.", null, data.GetRawText(), cancellationToken);
                else if (session.State is MachineSessionStates.DeliveryFailed or MachineSessionStates.RefundPending or
                         MachineSessionStates.Refunded or MachineSessionStates.ReconciliationRequired)
                {
                    await RecordConflictingDeliveryAsync(session, cancellationToken);
                    await AddEventAsync(session, "delivery.conflicting_result", "Success recebido depois de failure.", null, data.GetRawText(), cancellationToken);
                }
                return;
            }

            session.State = MachineSessionStates.Completed;
            session.DeliveryDeadlineAt = null;
            if (session.Transaction is { Status: "Approved" } transaction)
                await RecordDeliverySaleAsync(session, transaction, "Settled", 0m, cancellationToken);
            if (session.Transaction != null) session.Transaction.Status = "Approved";
            await AddEventAsync(session, "delivery.success", "Entrega confirmada pela máquina.", null, data.GetRawText(), cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            await transactionScope.CommitAsync(cancellationToken);
        }
        else if (session.State == MachineSessionStates.Completed)
        {
            await AddEventAsync(session, "delivery.duplicate_result", "Success duplicado recebido pela máquina.", null, data.GetRawText(), cancellationToken);
        }
        else if (session.State is MachineSessionStates.DeliveryFailed or MachineSessionStates.RefundPending or
                 MachineSessionStates.Refunded or MachineSessionStates.ReconciliationRequired)
        {
            await RecordConflictingDeliveryAsync(session, cancellationToken);
            await AddEventAsync(session, "delivery.conflicting_result", "Success recebido depois de failure.", null, data.GetRawText(), cancellationToken);
        }
        else if (session.Transaction != null && session.ItemNumber.HasValue)
        {
            await RecordConflictingDeliveryAsync(session, cancellationToken);
            await AddEventAsync(
                session,
                "delivery.conflicting_result",
                "Success recebido depois do encerramento ou da transição de pagamento da sessão.",
                null,
                data.GetRawText(),
                cancellationToken);
        }
    }

    private async Task RecordConflictingDeliveryAsync(MachineSession session, CancellationToken cancellationToken)
    {
        if (session.Transaction == null || !session.ItemNumber.HasValue)
            return;

        await using var transactionScope = await _db.Database.BeginTransactionAsync(cancellationToken);
        var (financialStatus, refundedAmount) = GetConflictingDeliveryFinancialState(session.Transaction);
        await RecordDeliverySaleAsync(session, session.Transaction, financialStatus, refundedAmount, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await transactionScope.CommitAsync(cancellationToken);
        await SynchronizeRefundedSaleAsync(session.Transaction.Id, cancellationToken);
    }

    private async Task SynchronizeRefundedSaleAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        var transaction = await _db.PaymentTransactions.AsNoTracking()
            .Where(item => item.Id == transactionId)
            .Select(item => new { item.CompanyId, item.MachineId, item.Status, item.Amount })
            .FirstOrDefaultAsync(cancellationToken);
        if (transaction?.Status != "Refunded")
            return;

        await _db.SaleRecords
            .Where(sale => sale.TransactionId == transactionId &&
                           sale.CompanyId == transaction.CompanyId &&
                           sale.MachineId == transaction.MachineId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(sale => sale.RefundedAmount, transaction.Amount)
                .SetProperty(sale => sale.FinancialStatus, "Refunded"), cancellationToken);
    }

    private static (string FinancialStatus, decimal RefundedAmount) GetConflictingDeliveryFinancialState(
        PaymentTransaction transaction) => transaction.Status switch
    {
        "Approved" => ("Settled", 0m),
        "Refunded" => ("Refunded", transaction.Amount),
        "Chargeback" when transaction.MercadoPagoStatusDetail == "settled" => ("Chargeback", transaction.Amount),
        "Chargeback" => ("Disputed", 0m),
        "Disputed" => ("Disputed", 0m),
        _ => ("ReconciliationRequired", 0m)
    };

    private async Task RecordDeliverySaleAsync(
        MachineSession session,
        PaymentTransaction transaction,
        string financialStatus,
        decimal refundedAmount,
        CancellationToken cancellationToken)
    {
        if (!session.ItemNumber.HasValue)
            return;
        if (!await MachineOperationLock.AcquireAsync(_db, session.MachineId, session.CompanyId, cancellationToken))
            throw new InvalidOperationException("sale_machine_not_found");
        if (await _db.SaleRecords.AnyAsync(sale => sale.TransactionId == transaction.Id, cancellationToken))
            return;

        var slot = await _db.MachineProductSlots.AsNoTracking().FirstOrDefaultAsync(
            candidate => candidate.CompanyId == session.CompanyId &&
                         candidate.MachineId == session.MachineId &&
                         candidate.ItemNumber == session.ItemNumber.Value,
            cancellationToken);
        var productId = session.SelectedProductIdSnapshot ?? slot?.ProductId;
        var productName = session.SelectedProductIdSnapshot != null
            ? session.SelectedProductNameSnapshot
            : slot == null
                ? null
                : await _db.Products.Where(product => product.Id == slot.ProductId &&
                                                      (product.CompanyId == null || product.CompanyId == session.CompanyId))
                    .Select(product => product.Name).FirstOrDefaultAsync(cancellationToken);
        var sale = new SaleRecord
        {
            CompanyId = session.CompanyId,
            MachineId = session.MachineId,
            SessionId = session.Id,
            TransactionId = transaction.Id,
            ItemNumber = session.ItemNumber.Value,
            ProductId = productId,
            ProductNameSnapshot = productName,
            LocationIdSnapshot = session.LocationIdSnapshot,
            LocationNameSnapshot = session.LocationNameSnapshot,
            Quantity = 1,
            GrossAmount = transaction.Amount,
            ApplicationFee = transaction.ApplicationFee,
            NetAmount = transaction.Amount - transaction.ApplicationFee,
            RefundedAmount = Math.Clamp(refundedAmount, 0m, transaction.Amount),
            FinancialStatus = financialStatus,
            SoldAt = DateTime.UtcNow
        };
        _db.SaleRecords.Add(sale);

        if (slot == null)
            return;

        if (session.SelectedProductIdSnapshot is string selectedProductId &&
            !string.Equals(slot.ProductId, selectedProductId, StringComparison.Ordinal))
        {
            var mappingReason = $"Venda confirmada para o produto selecionado na sessão no compartimento {session.ItemNumber.Value}, após remapeamento. O saldo não foi baixado automaticamente; um Admin deve conferir fisicamente e registrar o ajuste necessário.";
            _logger.LogWarning(
                "Venda {SaleId} da máquina {MachineId}, compartimento {ItemNumber}, chegou após o remapeamento do produto; baixa automática de estoque ignorada.",
                sale.Id,
                session.MachineId,
                session.ItemNumber.Value);
            _db.InventoryMovements.Add(new InventoryMovement
            {
                CompanyId = session.CompanyId,
                MachineId = session.MachineId,
                ItemNumber = session.ItemNumber.Value,
                ProductId = slot.ProductId,
                SaleRecordId = sale.Id,
                MovementType = "SaleMappingMismatch",
                QuantityDelta = 0,
                QuantityAfter = slot.CurrentStock,
                Reason = mappingReason,
                CreatedAt = sale.SoldAt
            });
            return;
        }

        var stockUpdated = await _db.MachineProductSlots
            .Where(candidate => candidate.CompanyId == session.CompanyId &&
                                candidate.MachineId == session.MachineId &&
                                candidate.ItemNumber == session.ItemNumber.Value)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.CurrentStock, candidate => candidate.CurrentStock - 1)
                .SetProperty(candidate => candidate.UpdatedAt, sale.SoldAt), cancellationToken);
        if (stockUpdated == 0)
            return;

        var stockAfterDelivery = await _db.MachineProductSlots.AsNoTracking()
            .Where(candidate => candidate.CompanyId == session.CompanyId &&
                                candidate.MachineId == session.MachineId &&
                                candidate.ItemNumber == session.ItemNumber.Value)
            .Select(candidate => candidate.CurrentStock)
            .SingleAsync(cancellationToken);
        _db.InventoryMovements.Add(new InventoryMovement
        {
            CompanyId = session.CompanyId,
            MachineId = session.MachineId,
            ItemNumber = session.ItemNumber.Value,
            ProductId = sale.ProductId ?? slot.ProductId,
            SaleRecordId = sale.Id,
            MovementType = "Sale",
            QuantityDelta = -1,
            QuantityAfter = stockAfterDelivery,
            Reason = "Entrega confirmada pela máquina.",
            CreatedAt = sale.SoldAt
        });
    }

    private async Task HandleDeliveryFailureAsync(MachineSession session, JsonElement data, string rawData, CancellationToken cancellationToken)
    {
        if (session.State is MachineSessionStates.PaymentApproved or MachineSessionStates.AwaitingDeliveryResult)
        {
            await using var transactionScope = await _db.Database.BeginTransactionAsync(cancellationToken);
            var claimed = await _db.MachineSessions
                .Where(item => item.Id == session.Id &&
                    (item.State == MachineSessionStates.PaymentApproved ||
                     item.State == MachineSessionStates.AwaitingDeliveryResult))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.State, MachineSessionStates.DeliveryFailed)
                    .SetProperty(item => item.DeliveryDeadlineAt, (DateTime?)null), cancellationToken);

            if (claimed == 0)
            {
                await transactionScope.RollbackAsync(cancellationToken);
                await _db.Entry(session).ReloadAsync(cancellationToken);
                if (session.State == MachineSessionStates.Completed)
                    await AddEventAsync(session, "delivery.conflicting_result", "Failure recebido depois de success.", null, rawData, cancellationToken);
                return;
            }

            var original = GetString(data, "reason") ?? "unknown";
            var normalized = original is "out_of_cup" or "out_of_product" or "mechanism_error" ? original : "unknown";
            if (session.TransactionId.HasValue && !await _db.DeliveryFailures.AnyAsync(x => x.TransactionId == session.TransactionId, cancellationToken))
            {
                _db.DeliveryFailures.Add(new DeliveryFailure
                {
                    CompanyId = session.CompanyId,
                    MachineId = session.MachineId,
                    SessionId = session.Id,
                    TransactionId = session.TransactionId.Value,
                    Reason = normalized,
                    OriginalReason = original
                });
            }
            session.State = MachineSessionStates.DeliveryFailed;
            session.DeliveryDeadlineAt = null;
            await AddEventAsync(session, "delivery.failure", normalized, null, rawData, cancellationToken);
            var refundQueued = await QueueRefundAsync(session, $"delivery_failure:{normalized}", cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            await transactionScope.CommitAsync(cancellationToken);
            if (refundQueued) await ProcessOutboxAsync(cancellationToken);
            await BroadcastSessionAsync(session);
        }
        else if (session.State == MachineSessionStates.Completed)
        {
            await AddEventAsync(session, "delivery.conflicting_result", "Failure recebido depois de success.", null, rawData, cancellationToken);
        }
    }

    private async Task HandleEndSessionAsync(MachineSession session, JsonElement data, CancellationToken cancellationToken)
    {
        var processFinancialOutbox = false;

        if (session.State == MachineSessionStates.PaymentPending)
        {
            await using var transactionScope = await _db.Database.BeginTransactionAsync(cancellationToken);
            var closedAt = DateTime.UtcNow;
            var claimed = await _db.MachineSessions
                .Where(item => item.Id == session.Id && item.State == MachineSessionStates.PaymentPending)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.State, MachineSessionStates.Denied)
                    .SetProperty(item => item.CloseReason, "device_closed_during_payment")
                    .SetProperty(item => item.ClosedAt, closedAt)
                    .SetProperty(item => item.PaymentDeadlineAt, (DateTime?)null), cancellationToken);

            if (claimed == 0)
            {
                await transactionScope.RollbackAsync(cancellationToken);
                await _db.Entry(session).ReloadAsync(cancellationToken);
                if (session.Transaction != null)
                    await _db.Entry(session.Transaction).ReloadAsync(cancellationToken);
                await HandleEndSessionAsync(session, data, cancellationToken);
                return;
            }

            session.State = MachineSessionStates.Denied;
            session.CloseReason = "device_closed_during_payment";
            session.ClosedAt = closedAt;
            session.PaymentDeadlineAt = null;
            session.SelectionDeadlineAt = null;
            session.DeliveryDeadlineAt = null;
            if (session.Transaction != null)
            {
                session.Transaction.Status = "CancellationPending";
                await EnqueueOutboxAsync(session, "cancel_payment", new { reason = session.CloseReason }, cancellationToken);
            }
            await AddEventAsync(session, "payment.cancelled", session.CloseReason, null, null, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            await transactionScope.CommitAsync(cancellationToken);
            return;
        }

        if (session.State is MachineSessionStates.PaymentApproved or MachineSessionStates.AwaitingDeliveryResult)
        {
            await using var transactionScope = await _db.Database.BeginTransactionAsync(cancellationToken);
            var closedAt = DateTime.UtcNow;
            var claimed = await _db.MachineSessions
                .Where(item => item.Id == session.Id &&
                    (item.State == MachineSessionStates.PaymentApproved ||
                     item.State == MachineSessionStates.AwaitingDeliveryResult))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.State, MachineSessionStates.RefundPending)
                    .SetProperty(item => item.CloseReason, item => item.CloseReason ?? "delivery_result_missing")
                    .SetProperty(item => item.ClosedAt, closedAt)
                    .SetProperty(item => item.DeliveryDeadlineAt, (DateTime?)null), cancellationToken);

            if (claimed == 0)
            {
                await transactionScope.RollbackAsync(cancellationToken);
                await _db.Entry(session).ReloadAsync(cancellationToken);
                if (session.Transaction != null)
                    await _db.Entry(session.Transaction).ReloadAsync(cancellationToken);
                await HandleEndSessionAsync(session, data, cancellationToken);
                return;
            }

            session.State = MachineSessionStates.RefundPending;
            session.CloseReason ??= "delivery_result_missing";
            session.ClosedAt = closedAt;
            session.SelectionDeadlineAt = null;
            session.PaymentDeadlineAt = null;
            session.DeliveryDeadlineAt = null;
            await AddEventAsync(
                session,
                "delivery.result_missing",
                "A máquina encerrou a sessão sem informar success ou failure.",
                null,
                data.GetRawText(),
                cancellationToken);
            processFinancialOutbox = await QueueRefundAsync(session, "delivery_result_missing", cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            await transactionScope.CommitAsync(cancellationToken);
            if (processFinancialOutbox)
                await ProcessOutboxAsync(cancellationToken);
            return;
        }

        if (session.State == MachineSessionStates.ClosingWithoutSelection)
            session.State = MachineSessionStates.ClosedWithoutSelection;
        else if (session.State is MachineSessionStates.Opening or MachineSessionStates.DeviceClosingBeforeSelection or MachineSessionStates.AwaitingSelection)
        {
            session.State = MachineSessionStates.ClosedByDeviceBeforeSelection;
            session.CloseReason = "device_closed_before_selection";
            await EvaluateEarlyCloseAlertAsync(session, cancellationToken);
        }
        else if (session.State == MachineSessionStates.DeniedAwaitingClosure)
            session.State = MachineSessionStates.Denied;
        // O encerramento físico é durável antes de qualquer chamada ao provedor. A
        // intenção financeira é salva na mesma unidade de trabalho e pode ser retomada
        // pelo worker mesmo que o processamento imediato falhe.
        session.ClosedAt = DateTime.UtcNow;
        session.SelectionDeadlineAt = null;
        session.PaymentDeadlineAt = null;
        session.DeliveryDeadlineAt = null;
        await _db.SaveChangesAsync(cancellationToken);

        if (processFinancialOutbox)
            await ProcessOutboxAsync(cancellationToken);
    }

    private async Task EvaluateEarlyCloseAlertAsync(MachineSession session, CancellationToken cancellationToken)
    {
        var window = DateTime.UtcNow.AddMinutes(-15);
        var count = await _db.MachineSessions.CountAsync(x => x.MachineId == session.MachineId &&
            x.CloseReason == "device_closed_before_selection" && x.ClosedAt >= window, cancellationToken) + 1;
        if (count < 3) return;
        var cooldown = DateTime.UtcNow.AddMinutes(-30);
        var hasRecentAlert = await _db.TelemetryEvents.AnyAsync(x => x.MachineId == session.MachineId &&
            x.EventType == "alert.repeated_early_close" && x.CreatedAt >= cooldown, cancellationToken);
        if (!hasRecentAlert)
            await AddEventAsync(session, "alert.repeated_early_close", $"{count} encerramentos antes da seleção em 15 minutos.", null, null, cancellationToken);
    }

    private async Task<bool> CancelPendingPaymentAsync(
        MachineSession session,
        string reason,
        Guid? userId,
        CancellationToken cancellationToken,
        bool notifyMachine = true)
    {
        await using var transactionScope = await _db.Database.BeginTransactionAsync(cancellationToken);
        var claimed = await _db.MachineSessions
            .Where(item => item.Id == session.Id && item.State == MachineSessionStates.PaymentPending)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.State, MachineSessionStates.DeniedAwaitingClosure)
                .SetProperty(item => item.PaymentDeadlineAt, (DateTime?)null), cancellationToken);

        if (claimed == 0)
        {
            await transactionScope.RollbackAsync(cancellationToken);
            await _db.Entry(session).ReloadAsync(cancellationToken);
            if (session.Transaction != null)
                await _db.Entry(session.Transaction).ReloadAsync(cancellationToken);
            return false;
        }

        session.State = MachineSessionStates.DeniedAwaitingClosure;
        session.CloseReason = reason;
        session.PaymentDeadlineAt = null;
        if (session.Transaction != null)
        {
            session.Transaction.Status = "CancellationPending";
            await EnqueueOutboxAsync(session, "cancel_payment", new { reason }, cancellationToken);
        }
        await AddEventAsync(session, "payment.cancelled", reason, userId, null, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await transactionScope.CommitAsync(cancellationToken);
        if (notifyMachine && session.Machine != null)
            await SendTrackedCommandAsync(session.Machine, session, "VENDA_NEGADA", userId, cancellationToken);
        return true;
    }

    private async Task DenyWithoutPaymentAsync(MachineSession session, string reason, CancellationToken cancellationToken)
    {
        session.State = MachineSessionStates.DeniedAwaitingClosure;
        session.CloseReason = reason;
        session.SelectionDeadlineAt = null;
        await AddEventAsync(session, "sale.denied", reason, null, null, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        var machine = session.Machine ?? await _db.Machines.FirstAsync(x => x.Id == session.MachineId, cancellationToken);
        await SendTrackedCommandAsync(machine, session, "VENDA_NEGADA", null, cancellationToken);
    }

    private async Task HandleMdbStatusAsync(
        InboundDeviceMessage message,
        JsonElement data,
        CancellationToken cancellationToken)
    {
        var rawStatus = GetString(data, "status");
        var normalizedStatus = MdbStatuses.Normalize(rawStatus);
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        if (!await MachineOperationLock.AcquireAsync(_db, message.MachineId, message.CompanyId, cancellationToken))
            return;
        // Lock the connection row as well: reconnects update this row independently
        // of the machine lock and must not race an old status into a new connection.
        await _db.MachineConnectionStates.Where(item => item.MachineId == message.MachineId && item.CompanyId == message.CompanyId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ConnectionId, item => item.ConnectionId), cancellationToken);
        var state = await _db.MachineConnectionStates.FirstOrDefaultAsync(
            x => x.MachineId == message.MachineId && x.CompanyId == message.CompanyId, cancellationToken);
        if (state == null)
        {
            state = new MachineConnectionState { MachineId = message.MachineId, CompanyId = message.CompanyId };
            _db.MachineConnectionStates.Add(state);
        }
        else
            await _db.Entry(state).ReloadAsync(cancellationToken);
        // An old connection or delayed status must never advance the sequence.
        if ((state.ConnectionId != null && state.ConnectionId != message.ConnectionId) ||
            (state.MdbStatusUpdatedAt != null && message.ReceivedAt <= state.MdbStatusUpdatedAt))
            return;

        var previousStatus = state.MdbStatus;
        var previousRaw = state.MdbStatusRaw;
        var staleSeconds = Math.Clamp(_configuration.GetValue<int?>("MdbStatus:StaleAfterSeconds") ?? 90, 10, 3600);
        var fresh = message.ReceivedAt >= DateTime.UtcNow.AddSeconds(-staleSeconds);
        var sequenceFresh = fresh && state.MdbMenuConnectionId == message.ConnectionId &&
            state.MdbMenuUpdatedAt != null && message.ReceivedAt >= state.MdbMenuUpdatedAt &&
            message.ReceivedAt - state.MdbMenuUpdatedAt <= TimeSpan.FromSeconds(staleSeconds);
        var completedMenu = sequenceFresh && (state.MdbMenuPhase == "inactive" || state.MdbMenuPhase == "ready") && normalizedStatus == MdbStatuses.Enabled;
        var completedSessionId = completedMenu ? state.MdbMenuSessionId : null;
        MachineSession? closedSession = null;
        if (fresh && normalizedStatus == MdbStatuses.Enabled)
        {
            var pendingClosure = await _db.MachineSessions.Where(item => item.MachineId == message.MachineId &&
                item.CompanyId == message.CompanyId && item.ItemNumber == null && item.TransactionId == null &&
                (item.State == MachineSessionStates.ClosingWithoutSelection || item.State == MachineSessionStates.ClosureIncomplete))
                .OrderByDescending(item => item.StartedAt).FirstOrDefaultAsync(cancellationToken);
            if (pendingClosure != null && await CompleteResetClosureAsync(pendingClosure.Id, message.CompanyId, null,
                    new DeviceStatusResult(true, message.MessageId, normalizedStatus, message.DataJson, message.ReceivedAt), cancellationToken))
            {
                closedSession = await _db.MachineSessions.FirstAsync(item => item.Id == pendingClosure.Id, cancellationToken);
                await _db.Entry(closedSession).ReloadAsync(cancellationToken);
                completedMenu = true;
                completedSessionId = pendingClosure.Id;
                state.MdbMenuPhase = "ready";
                state.MdbMenuConnectionId = message.ConnectionId;
                state.MdbMenuSessionId = pendingClosure.Id;
                state.MdbMenuUpdatedAt = message.ReceivedAt;
            }
        }
        if (closedSession == null && completedMenu && completedSessionId.HasValue)
        {
            var closedAt = DateTime.UtcNow;
            var updated = await _db.MachineSessions.Where(item => item.Id == completedSessionId &&
                    item.MachineId == message.MachineId && item.CompanyId == message.CompanyId &&
                    item.ClosedAt == null && item.TransactionId == null && item.ItemNumber == null &&
                    (item.State == MachineSessionStates.Opening || item.State == MachineSessionStates.AwaitingSelection ||
                     item.State == MachineSessionStates.DeviceClosingBeforeSelection))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.State, MachineSessionStates.ClosedByDeviceBeforeSelection)
                    .SetProperty(item => item.CloseReason, "menu_closed_without_selection")
                    .SetProperty(item => item.SelectionDeadlineAt, (DateTime?)null)
                    .SetProperty(item => item.ClosedAt, closedAt)
                    .SetProperty(item => item.LastEventAt, closedAt), cancellationToken);
            if (updated == 1)
            {
                closedSession = await _db.MachineSessions.FirstAsync(item => item.Id == completedSessionId, cancellationToken);
                await _db.Entry(closedSession).ReloadAsync(cancellationToken);
                await AddEventAsync(closedSession, "session.menu_closed", "Menu fechado sem seleção; sessão encerrada.", null, message.DataJson, cancellationToken);
            }
        }

        if (completedMenu)
        {
            // Keep the completed sequence durable until the next status change, so a crash
            // before automatic opening can be retried by the next enabled response.
            state.MdbMenuPhase = "ready";
            state.MdbMenuUpdatedAt = message.ReceivedAt;
        }
        else if (fresh && normalizedStatus == MdbStatuses.Idle && (!sequenceFresh || state.MdbMenuPhase != "inactive"))
        {
            state.MdbMenuPhase = "idle";
            state.MdbMenuConnectionId = message.ConnectionId;
            state.MdbMenuSessionId = await ActiveSessions().AsNoTracking()
                .Where(item => item.MachineId == message.MachineId && item.CompanyId == message.CompanyId)
                .Select(item => (Guid?)item.Id).FirstOrDefaultAsync(cancellationToken);
            state.MdbMenuUpdatedAt = message.ReceivedAt;
        }
        else if (sequenceFresh && normalizedStatus == MdbStatuses.Inactive &&
                 (state.MdbMenuPhase == "idle" || state.MdbMenuPhase == "inactive"))
        {
            state.MdbMenuPhase = "inactive";
            state.MdbMenuUpdatedAt = message.ReceivedAt;
        }
        else
        {
            state.MdbMenuPhase = null;
            state.MdbMenuConnectionId = null;
            state.MdbMenuSessionId = null;
            state.MdbMenuUpdatedAt = null;
        }
        state.MdbStatus = normalizedStatus;
        state.MdbStatusRaw = message.DataJson;
        state.MdbStatusUpdatedAt = message.ReceivedAt;
        if (!string.Equals(previousStatus, normalizedStatus, StringComparison.Ordinal) ||
            (normalizedStatus == null && !string.Equals(previousRaw, message.DataJson, StringComparison.Ordinal)))
        {
            _db.TelemetryEvents.Add(new TelemetryEvent
            {
                CompanyId = message.CompanyId, MachineId = message.MachineId,
                EventType = normalizedStatus == null ? "mdb.status.invalid" : "mdb.status.changed",
                Detail = normalizedStatus == null ? $"Status MDB não reconhecido: {rawStatus ?? "(ausente)"}." : $"{previousStatus ?? "unknown"} -> {normalizedStatus}",
                DataJson = message.DataJson, CreatedAt = message.ReceivedAt
            });
        }
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await transaction.DisposeAsync();
        if (closedSession != null) await BroadcastSessionAsync(closedSession);
        await _panelHub.BroadcastCompanyAsync(message.CompanyId, new
        {
            type = "mdb.status.updated", machineId = message.MachineId, mdbStatus = normalizedStatus,
            mdbStatusRaw = rawStatus, mdbStatusUpdatedAt = message.ReceivedAt, mdbStatusFresh = fresh && normalizedStatus != null,
            mdbMenuSelectionActive = state.MdbMenuPhase == "inactive"
        });
        if (fresh && normalizedStatus == MdbStatuses.Enabled)
            await TryAutoOpenSessionAsync(message, state, completedMenu, completedSessionId, cancellationToken);
    }

    private async Task TryAutoOpenSessionAsync(
        InboundDeviceMessage message,
        MachineConnectionState connectionState,
        bool completedMenu,
        Guid? completedSessionId,
        CancellationToken cancellationToken)
    {
        var machine = await _db.Machines.FirstOrDefaultAsync(
            x => x.Id == message.MachineId && x.CompanyId == message.CompanyId,
            cancellationToken);
        if (machine?.AutoOpenSessionEnabled != true || !connectionState.MonitoringEnabled)
            return;

        var admissionGate = await new SessionAdmissionGateService(_db).GetAsync(cancellationToken);
        if (admissionGate?.NewSessionsPaused != false ||
            !await _connections.IsOnlineAsync(machine.SerialNumber, cancellationToken))
        {
            return;
        }

        if (await ActiveSessions().AnyAsync(
                x => x.MachineId == message.MachineId && x.CompanyId == message.CompanyId,
                cancellationToken))
        {
            return;
        }

        var lastClosed = await _db.MachineSessions.AsNoTracking()
            .Where(x => x.MachineId == message.MachineId && x.CompanyId == message.CompanyId && x.ClosedAt != null)
            .OrderByDescending(x => x.ClosedAt)
            .Select(x => new { x.Id, x.ClosedAt })
            .FirstOrDefaultAsync(cancellationToken);
        var reopensMenuSession = completedMenu && completedSessionId.HasValue && lastClosed?.Id == completedSessionId;
        if (!reopensMenuSession && lastClosed?.ClosedAt is DateTime lastClosedAt && message.ReceivedAt <= lastClosedAt)
        {
            return;
        }

        var cooldownSeconds = Math.Clamp(
            _configuration.GetValue<int?>("MdbStatus:AutoOpenCooldownSeconds") ?? 30,
            1,
            3600);
        if (!reopensMenuSession && connectionState.AutoOpenLastAttemptAt >= DateTime.UtcNow.AddSeconds(-cooldownSeconds))
        {
            return;
        }

        connectionState.AutoOpenLastAttemptAt = DateTime.UtcNow;
        connectionState.AutoOpenLastError = null;
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            await StartSessionAsync(
                message.MachineId,
                message.CompanyId,
                null,
                "automatic_mdb",
                cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex.Message == "session_already_active")
        {
            // Outra requisição venceu a corrida; a restrição no banco preservou a idempotência.
        }
        catch (Exception ex)
        {
            connectionState.AutoOpenLastError = ex is InvalidOperationException
                ? ex.Message
                : "automatic_session_failed";
            _db.TelemetryEvents.Add(new TelemetryEvent
            {
                CompanyId = message.CompanyId,
                MachineId = message.MachineId,
                EventType = "session.auto_open_failed",
                Detail = connectionState.AutoOpenLastError
            });
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogWarning(
                ex,
                "Falha ao abrir sessão automática para a máquina {MachineId}.",
                message.MachineId);
        }
    }

    private async Task BeginRefundAsync(MachineSession session, string reason, CancellationToken cancellationToken)
    {
        if (!await QueueRefundAsync(session, reason, cancellationToken)) return;
        await _db.SaveChangesAsync(cancellationToken);
        await ProcessOutboxAsync(cancellationToken);
        await BroadcastSessionAsync(session);
    }

    private async Task<bool> QueueRefundAsync(
        MachineSession session,
        string reason,
        CancellationToken cancellationToken)
    {
        if (session.Transaction == null && session.TransactionId.HasValue)
            session.Transaction = await _db.PaymentTransactions.FirstOrDefaultAsync(x => x.Id == session.TransactionId, cancellationToken);
        if (session.Transaction == null) return false;
        if (session.Transaction.Status is "Refunded" or "RefundPending" or "ReconciliationRequired") return false;

        session.State = MachineSessionStates.RefundPending;
        session.Transaction.Status = "RefundPending";
        session.DeliveryDeadlineAt = null;
        await AddEventAsync(session, "refund.requested", reason, null, null, cancellationToken);
        await EnqueueOutboxAsync(session, "refund_payment", new { reason }, cancellationToken);
        return true;
    }

    private async Task<bool> RefundProviderPaymentAsync(PaymentTransaction transaction, CancellationToken cancellationToken)
    {
        if (transaction.MercadoPagoPaymentId?.StartsWith("mock_", StringComparison.OrdinalIgnoreCase) == true) return true;
        if (string.IsNullOrWhiteSpace(transaction.MercadoPagoPaymentId))
        {
            _logger.LogWarning(
                "Estorno da transação {TransactionId} requer reconciliação porque não há ID do pagamento no Mercado Pago.",
                transaction.Id);
            return false;
        }

        try
        {
            var integration = await _db.MercadoPagoIntegrations.FirstOrDefaultAsync(x => x.CompanyId == transaction.CompanyId && x.IsActive, cancellationToken);
            if (integration == null) return false;
            var client = _httpClientFactory.CreateClient("MercadoPago");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", EncryptionService.Decrypt(integration.AccessToken));

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://api.mercadopago.com/v1/payments/{transaction.MercadoPagoPaymentId}/refunds")
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-Idempotency-Key", $"refund-{transaction.Id}");

            using var response = await client.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao estornar transação {TransactionId}.", transaction.Id);
            return false;
        }
    }

    private async Task<bool> CancelProviderPaymentAsync(PaymentTransaction transaction, CancellationToken cancellationToken)
    {
        if (transaction.MercadoPagoPaymentId?.StartsWith("mock_", StringComparison.OrdinalIgnoreCase) == true) return true;
        if (string.IsNullOrWhiteSpace(transaction.MercadoPagoPaymentId))
        {
            _logger.LogWarning(
                "Cancelamento da transação {TransactionId} requer reconciliação porque não há ID do pagamento no Mercado Pago.",
                transaction.Id);
            return false;
        }

        try
        {
            var integration = await _db.MercadoPagoIntegrations.FirstOrDefaultAsync(x => x.CompanyId == transaction.CompanyId && x.IsActive, cancellationToken);
            if (integration == null) return false;
            var client = _httpClientFactory.CreateClient("MercadoPago");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", EncryptionService.Decrypt(integration.AccessToken));
            var response = await client.PutAsJsonAsync($"https://api.mercadopago.com/v1/payments/{transaction.MercadoPagoPaymentId}", new { status = "cancelled" }, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao cancelar transação {TransactionId}.", transaction.Id);
            return false;
        }
    }

    private async Task EnqueueOutboxAsync(
        MachineSession session,
        string messageType,
        object payload,
        CancellationToken cancellationToken)
    {
        if (_db.OutboxMessages.Local.Any(x => x.SessionId == session.Id && x.MessageType == messageType) ||
            await _db.OutboxMessages.AnyAsync(
                x => x.SessionId == session.Id && x.MessageType == messageType,
                cancellationToken))
        {
            return;
        }

        _db.OutboxMessages.Add(new OutboxMessage
        {
            CompanyId = session.CompanyId,
            MachineId = session.MachineId,
            SessionId = session.Id,
            TransactionId = session.TransactionId,
            MessageType = messageType,
            PayloadJson = JsonSerializer.Serialize(payload),
            NextAttemptAt = DateTime.UtcNow,
            Status = string.IsNullOrWhiteSpace(_configuration["Telemetry:LegacyApiBaseUrl"])
                ? "pending"
                : "deferred_candidate_overlap"
        });
    }

    private async Task<DeviceCommandResult> SendTrackedCommandAsync(
        Machine machine,
        MachineSession session,
        object command,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        var commandName = command is string text ? text : JsonSerializer.Serialize(command);
        var result = await _connections.SendCommandAsync(machine.SerialNumber, command, cancellationToken);
        _db.TelemetryCommands.Add(new TelemetryCommand
        {
            CompanyId = session.CompanyId,
            MachineId = session.MachineId,
            SessionId = session.Id,
            TransactionId = session.TransactionId,
            UserId = userId,
            MessageId = result.MessageId,
            Command = commandName,
            DataJson = command is string ? null : commandName,
            Status = result.Success ? "acknowledged" : "failed",
            Attempts = result.Attempts,
            SentAt = DateTime.UtcNow,
            AckAt = result.Success ? DateTime.UtcNow : null,
            Error = result.Error
        });
        await _db.SaveChangesAsync(cancellationToken);
        return result;
    }

    private Task AddEventAsync(MachineSession session, string type, string? detail, Guid? userId, string? dataJson, CancellationToken _)
    {
        _db.TelemetryEvents.Add(new TelemetryEvent
        {
            CompanyId = session.CompanyId,
            MachineId = session.MachineId,
            SessionId = session.Id,
            TransactionId = session.TransactionId,
            UserId = userId,
            EventType = type,
            Detail = detail,
            DataJson = dataJson
        });
        return Task.CompletedTask;
    }

    private Task AddStandaloneEventAsync(InboundDeviceMessage message, string type, string detail, string? dataJson, CancellationToken _)
    {
        _db.TelemetryEvents.Add(new TelemetryEvent
        {
            CompanyId = message.CompanyId,
            MachineId = message.MachineId,
            EventType = type,
            Detail = detail,
            DataJson = dataJson
        });
        return _db.SaveChangesAsync();
    }

    private Task BroadcastSessionAsync(MachineSession session) => _panelHub.BroadcastCompanyAsync(session.CompanyId, new
    {
        type = "sale.updated",
        machineId = session.MachineId,
        sessionId = session.Id,
        transactionId = session.TransactionId,
        state = session.State,
        itemNumber = session.ItemNumber,
        amountCents = session.AmountCents,
        closeReason = session.CloseReason,
        selectionDeadlineAt = session.SelectionDeadlineAt,
        paymentDeadlineAt = session.PaymentDeadlineAt,
        deliveryDeadlineAt = session.DeliveryDeadlineAt,
        closedAt = session.ClosedAt,
        updatedAt = session.LastEventAt
    });

    private IQueryable<MachineSession> ActiveSessions() =>
        _db.MachineSessions.Where(x => x.ClosedAt == null);

    private static TransactionCorrelation GetTransactionCorrelation(MachineSession session, JsonElement data)
    {
        if (!data.TryGetProperty("transactionId", out var transactionId) ||
            transactionId.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return TransactionCorrelation.Missing;

        if (transactionId.ValueKind != JsonValueKind.String)
            return TransactionCorrelation.Invalid;

        var value = transactionId.GetString();
        if (string.IsNullOrWhiteSpace(value))
            return TransactionCorrelation.Missing;

        if (!Guid.TryParse(value, out var parsed))
            return TransactionCorrelation.Invalid;

        return session.TransactionId.HasValue && parsed == session.TransactionId.Value
            ? TransactionCorrelation.Matched
            : TransactionCorrelation.Mismatch;
    }

    private enum TransactionCorrelation
    {
        Missing,
        Matched,
        Invalid,
        Mismatch
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsMissingTransactionId(JsonElement data) =>
        !data.TryGetProperty("transactionId", out var value) ||
        value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
        (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()));

    private static int GetInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var parsed) ? parsed : -1;

    private static bool IsOpenSessionConstraintViolation(DbUpdateException exception)
    {
        var text = exception.ToString();
        return text.Contains("UX_MachineSessions_OneOpenPerMachine", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("UNIQUE constraint failed: MachineSessions.MachineId", StringComparison.OrdinalIgnoreCase);
    }
}
