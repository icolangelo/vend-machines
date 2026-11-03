using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;

namespace VendingMachines.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class MachinesController : BaseApiController
{
    private readonly IDataService _dataService;
    private readonly AppDbContext _db;
    private readonly ITelemetryConnectionOperations _connections;
    private readonly IAuditLogService? _audit;
    private readonly ILogger<MachinesController>? _logger;

    public MachinesController(
        IDataService dataService,
        AppDbContext db,
        ITelemetryConnectionOperations connections,
        IAuditLogService? audit = null,
        ILogger<MachinesController>? logger = null)
    {
        _dataService = dataService;
        _db = db;
        _connections = connections;
        _audit = audit;
        _logger = logger;
    }

    [HttpGet]
    public ActionResult<IEnumerable<Machine>> GetAll()
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }

        return Ok(_dataService.GetMachines(companyId));
    }

    [HttpGet("{id}")]
    public ActionResult<Machine> GetById(string id)
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }

        var machine = _dataService.GetMachines(companyId).FirstOrDefault(m => m.Id == id);
        if (machine == null) return NotFound();
        return Ok(machine);
    }

    [Authorize(Roles = AppRoles.CompanyAdminOrPlatformAdmin)]
    [HttpPost]
    public ActionResult Create([FromBody] Machine machine)
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }

        machine.CompanyId = companyId;
        try
        {
            using var transaction = _db.Database.BeginTransaction();
            _dataService.AddMachine(machine);
            ApplyAutomaticSessionConfiguration(machine, companyId, previousValue: null);
            _db.SaveChanges();
            transaction.Commit();
            machine.Company = null;
            machine.AssignedLocation = null;
            return CreatedAtAction(nameof(GetById), new { id = machine.Id }, machine);
        }
        catch (InvalidOperationException ex)
        {
            if (!ex.Message.Contains("ID"))
            {
                return BadRequest(new { message = ex.Message });
            }

            return Conflict(new { message = ex.Message });
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("PK_Machines") == true || ex.Message.Contains("PK_Machines"))
        {
            return Conflict(new { message = "Já existe uma máquina cadastrada com este ID." });
        }
    }

    [Authorize(Roles = AppRoles.CompanyAdminOrPlatformAdmin)]
    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] Machine machine, CancellationToken cancellationToken)
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }

        var existing = await _db.Machines.FirstOrDefaultAsync(
            candidate => candidate.Id == id && candidate.CompanyId == companyId,
            cancellationToken);
        if (existing == null) return NotFound();

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        if (!await MachineOperationLock.AcquireAsync(_db, id, companyId, cancellationToken))
            return NotFound();
        await _db.Entry(existing).ReloadAsync(cancellationToken);

        var previousAutoOpenValue = existing.AutoOpenSessionEnabled;
        var requestedNormalizedSerial = string.IsNullOrWhiteSpace(machine.SerialNumber)
            ? null
            : machine.SerialNumber.Trim().ToUpperInvariant();
        var serialChanged = !string.Equals(
            existing.NormalizedSerialNumber,
            requestedNormalizedSerial,
            StringComparison.Ordinal);
        var deactivating = existing.IsActive && !machine.IsActive;
        if (serialChanged || deactivating)
        {
            var hasActiveSession = await _db.MachineSessions.AsNoTracking().AnyAsync(
                session => session.CompanyId == companyId && session.MachineId == id && session.ClosedAt == null,
                cancellationToken);
            var isOnline = await _connections.IsOnlineAsync(existing.SerialNumber, cancellationToken);
            if (hasActiveSession || isOnline)
            {
                return Conflict(new
                {
                    code = "machine_in_use",
                    message = "Encerre a sessão MDB e aguarde a desconexão da máquina antes de alterar o número de série ou desativá-la."
                });
            }
        }

        machine.CompanyId = companyId;
        machine.Id = id; // Garantir que o ID corresponda à URL
        try
        {
            _dataService.UpdateMachine(machine);
            ApplyAutomaticSessionConfiguration(machine, companyId, previousAutoOpenValue);
            _db.SaveChanges();
            await transaction.CommitAsync(cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [Authorize(Roles = AppRoles.CompanyAdminOrPlatformAdmin)]
    [HttpPost("{id}/telemetry-credential")]
    public async Task<IActionResult> RotateTelemetryCredential(string id, CancellationToken cancellationToken)
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }

        var machine = await _db.Machines.FirstOrDefaultAsync(
            x => x.Id == id && x.CompanyId == companyId,
            cancellationToken);
        if (machine == null) return NotFound();

        if (!await CanSafelyChangeOnlineMachineCredentialAsync(machine, cancellationToken))
            return CredentialOwnerUnavailable();

        var credential = DeviceCredentialService.Generate();
        var credentialHash = DeviceCredentialService.Hash(credential);
        var credentialCreatedAt = DateTime.UtcNow;
        var previousCredentialHash = machine.TelemetryCredentialHash;
        var previousCredentialCreatedAt = machine.TelemetryCredentialCreatedAt;
        var previousRevokedAt = machine.TelemetryCredentialRevokedAt;
        var claimed = await _db.Machines
            .Where(x => x.Id == id && x.CompanyId == companyId &&
                        x.TelemetryCredentialHash == previousCredentialHash &&
                        x.TelemetryCredentialCreatedAt == previousCredentialCreatedAt &&
                        x.TelemetryCredentialRevokedAt == previousRevokedAt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.TelemetryCredentialHash, credentialHash)
                .SetProperty(x => x.TelemetryCredentialCreatedAt, credentialCreatedAt)
                .SetProperty(x => x.TelemetryCredentialRevokedAt, (DateTime?)null),
                CancellationToken.None);
        if (claimed == 0)
        {
            return Conflict(new
            {
                code = "credential_changed",
                message = "A credencial da máquina foi alterada por outra operação. Atualize os dados antes de tentar novamente."
            });
        }

        var disconnect = await DisconnectForCredentialChangeAsync(machine.SerialNumber, "Credencial rotacionada.");
        if (!disconnect.Success)
        {
            var reverted = await _db.Machines
                .Where(x => x.Id == id && x.CompanyId == companyId &&
                            x.TelemetryCredentialHash == credentialHash &&
                            x.TelemetryCredentialCreatedAt == credentialCreatedAt &&
                            x.TelemetryCredentialRevokedAt == null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.TelemetryCredentialHash, previousCredentialHash)
                    .SetProperty(x => x.TelemetryCredentialCreatedAt, previousCredentialCreatedAt)
                    .SetProperty(x => x.TelemetryCredentialRevokedAt, previousRevokedAt),
                    CancellationToken.None);
            await RecordAuditAsync(
                "machine.telemetry_credential_rotation_failed", "Machine", machine.Id, companyId,
                new { error = disconnect.Error, credentialChangeRolledBack = reverted == 1 });
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                code = disconnect.Error ?? "machine_disconnect_unconfirmed",
                credentialChangeRolledBack = reverted == 1,
                message = "Não foi possível confirmar o encerramento da conexão MDB. Confira o estado da credencial e da máquina antes de tentar novamente."
            });
        }

        var credentialStillCurrent = await _db.Machines.AsNoTracking()
            .AnyAsync(x => x.Id == id && x.CompanyId == companyId &&
                           x.TelemetryCredentialHash == credentialHash &&
                           x.TelemetryCredentialCreatedAt == credentialCreatedAt &&
                           x.TelemetryCredentialRevokedAt == null,
                CancellationToken.None);
        if (!credentialStillCurrent)
        {
            await RecordAuditAsync("machine.telemetry_credential_rotation_superseded", "Machine", machine.Id, companyId);
            return Conflict(new
            {
                code = "credential_changed",
                message = "A credencial foi alterada novamente durante a operação. Consulte o estado atual antes de usar uma chave."
            });
        }

        await RecordAuditAsync("machine.telemetry_credential_rotated", "Machine", machine.Id, companyId);

        return Ok(new
        {
            machineId = machine.Id,
            credential,
            createdAt = credentialCreatedAt,
            message = "Guarde esta credencial. Ela será exibida somente agora."
        });
    }

    [Authorize(Roles = AppRoles.CompanyAdminOrPlatformAdmin)]
    [HttpDelete("{id}/telemetry-credential")]
    public async Task<IActionResult> RevokeTelemetryCredential(string id, CancellationToken cancellationToken)
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }

        var machine = await _db.Machines.FirstOrDefaultAsync(
            x => x.Id == id && x.CompanyId == companyId,
            cancellationToken);
        if (machine == null) return NotFound();

        if (!await CanSafelyChangeOnlineMachineCredentialAsync(machine, cancellationToken))
            return CredentialOwnerUnavailable();

        var previousRevokedAt = machine.TelemetryCredentialRevokedAt;
        var revokedAt = DateTime.UtcNow;
        var credentialHash = machine.TelemetryCredentialHash;
        var claimed = await _db.Machines
            .Where(x => x.Id == id && x.CompanyId == companyId &&
                        x.TelemetryCredentialHash == credentialHash &&
                        x.TelemetryCredentialRevokedAt == previousRevokedAt)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.TelemetryCredentialRevokedAt, revokedAt),
                CancellationToken.None);
        if (claimed == 0)
        {
            return Conflict(new
            {
                code = "credential_changed",
                message = "A credencial da máquina foi alterada por outra operação. Atualize os dados antes de tentar novamente."
            });
        }

        var disconnect = await DisconnectForCredentialChangeAsync(machine.SerialNumber, "Credencial revogada.");
        if (!disconnect.Success)
        {
            var reverted = await _db.Machines
                .Where(x => x.Id == id && x.CompanyId == companyId &&
                            x.TelemetryCredentialHash == credentialHash &&
                            x.TelemetryCredentialRevokedAt == revokedAt)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.TelemetryCredentialRevokedAt, previousRevokedAt),
                    CancellationToken.None);
            await RecordAuditAsync(
                "machine.telemetry_credential_revocation_failed", "Machine", machine.Id, companyId,
                new { error = disconnect.Error, credentialChangeRolledBack = reverted == 1 });
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                code = disconnect.Error ?? "machine_disconnect_unconfirmed",
                credentialChangeRolledBack = reverted == 1,
                message = "Não foi possível confirmar o encerramento da conexão MDB. Confira o estado da credencial e da máquina antes de tentar novamente."
            });
        }

        var revocationStillCurrent = await _db.Machines.AsNoTracking()
            .AnyAsync(x => x.Id == id && x.CompanyId == companyId &&
                           x.TelemetryCredentialHash == credentialHash &&
                           x.TelemetryCredentialRevokedAt == revokedAt,
                CancellationToken.None);
        if (!revocationStillCurrent)
        {
            await RecordAuditAsync("machine.telemetry_credential_revocation_superseded", "Machine", machine.Id, companyId);
            return Conflict(new
            {
                code = "credential_changed",
                message = "A credencial foi alterada novamente durante a operação. Consulte o estado atual antes de tentar novamente."
            });
        }

        await RecordAuditAsync("machine.telemetry_credential_revoked", "Machine", machine.Id, companyId);
        return NoContent();
    }

    private async Task<DeviceCommandResult> DisconnectForCredentialChangeAsync(string serial, string reason)
    {
        try
        {
            return await _connections.DisconnectMachineAsync(serial, reason, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Não foi possível confirmar o encerramento MDB durante a alteração de credencial da máquina {Serial}.", serial);
            return new DeviceCommandResult(false, 0, 0, "command_outcome_unknown");
        }
    }

    private async Task<bool> CanSafelyChangeOnlineMachineCredentialAsync(
        Machine machine,
        CancellationToken cancellationToken)
    {
        var sharedPresenceIsOnline = machine.CompanyId.HasValue &&
            await _db.MachineConnectionStates.AsNoTracking()
                .Where(state => state.MachineId == machine.Id && state.CompanyId == machine.CompanyId.Value)
                .Select(state => state.IsOnline)
                .FirstOrDefaultAsync(cancellationToken);
        if (!sharedPresenceIsOnline && !await _connections.IsOnlineAsync(machine.SerialNumber, cancellationToken))
            return true;

        // Rotating/revoking a credential intentionally closes the active socket.
        // Do not first mutate the shared credential when this process cannot route
        // that disconnect to the current owner (especially an older API instance).
        return await _connections.HasCommandRouteAsync(machine.SerialNumber, cancellationToken);
    }

    private static IActionResult CredentialOwnerUnavailable() => new ObjectResult(new
    {
        code = "machine_owner_unknown",
        message = "A máquina está conectada por um processo sem rota de desconexão. A credencial não foi alterada."
    })
    {
        StatusCode = StatusCodes.Status503ServiceUnavailable
    };

    private Task RecordAuditAsync(
        string action,
        string resourceType,
        string resourceId,
        Guid companyId,
        object? metadata = null) =>
        _audit?.RecordAsync(action, resourceType, resourceId, companyId, metadata, HttpContext.RequestAborted)
        ?? Task.CompletedTask;

    private void ApplyAutomaticSessionConfiguration(Machine machine, Guid companyId, bool? previousValue)
    {
        var hasChanged = previousValue.HasValue
            ? previousValue.Value != machine.AutoOpenSessionEnabled
            : machine.AutoOpenSessionEnabled;
        var userId = Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var parsedUserId)
            ? parsedUserId
            : (Guid?)null;

        if (hasChanged)
        {
            _db.TelemetryEvents.Add(new TelemetryEvent
            {
                CompanyId = companyId,
                MachineId = machine.Id,
                UserId = userId,
                EventType = machine.AutoOpenSessionEnabled
                    ? "machine.auto_open.enabled"
                    : "machine.auto_open.disabled"
            });
        }

        var state = _db.MachineConnectionStates.FirstOrDefault(x => x.MachineId == machine.Id);
        if (state != null && state.CompanyId != companyId)
        {
            if (hasChanged && machine.AutoOpenSessionEnabled)
            {
                throw new InvalidOperationException(
                    "Não foi possível ativar o acompanhamento desta máquina. Atualize o estado de telemetria e tente novamente.");
            }

            // Never change monitoring preferences or telemetry errors owned by another company.
            return;
        }

        if (!machine.AutoOpenSessionEnabled)
        {
            if (state != null)
            {
                state.AutoOpenLastError = null;
            }
            return;
        }

        if (state == null)
        {
            state = new MachineConnectionState
            {
                MachineId = machine.Id,
                CompanyId = companyId
            };
            _db.MachineConnectionStates.Add(state);
        }

        if (state.MonitoringEnabled)
        {
            return;
        }

        state.MonitoringEnabled = true;
        state.ActivatedAt = DateTime.UtcNow;
        state.ActivatedByUserId = userId;
        _db.TelemetryEvents.Add(new TelemetryEvent
        {
            CompanyId = companyId,
            MachineId = machine.Id,
            UserId = state.ActivatedByUserId,
            EventType = "monitoring.activated",
            Detail = "Acompanhamento ativado junto com a abertura automática."
        });
    }

}
