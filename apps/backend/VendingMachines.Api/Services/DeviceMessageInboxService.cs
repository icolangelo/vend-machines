using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;

namespace VendingMachines.Api.Services;

public sealed class DeviceMessageInboxService
{
    private const int DefaultMaximumPendingMessages = 5000;
    private const int AdmissionLockNamespace = 22093;
    private const int AdmissionLockId = 18766;
    private readonly AppDbContext _db;
    private readonly DeviceMessageInboxAdmissionGate _admissionGate;
    private readonly int _maximumPendingMessages;

    public DeviceMessageInboxService(
        AppDbContext db,
        IConfiguration configuration,
        DeviceMessageInboxAdmissionGate admissionGate)
    {
        _db = db;
        _admissionGate = admissionGate;
        _maximumPendingMessages = Math.Clamp(
            configuration.GetValue<int?>("Telemetry:MaxPendingInboundMessages") ?? DefaultMaximumPendingMessages,
            100,
            100_000);
    }

    public async Task<bool> PersistBeforeAcknowledgementAsync(InboundDeviceMessage message, CancellationToken cancellationToken)
    {
        await _admissionGate.Semaphore.WaitAsync(cancellationToken);
        try
        {
            if (_db.Database.IsNpgsql() && string.IsNullOrWhiteSpace(message.OwnerNodeId))
                return false;

            await using var transaction = await BeginAdmissionTransactionAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(message.OwnerNodeId))
            {
                MachineConnectionState? owner;
                if (_db.Database.IsNpgsql())
                {
                    var lockedOwners = await _db.MachineConnectionStates
                        .FromSqlInterpolated($"SELECT * FROM \"MachineConnectionStates\" WHERE \"MachineId\" = {message.MachineId} FOR UPDATE")
                        .ToListAsync(cancellationToken);
                    owner = lockedOwners.SingleOrDefault();
                }
                else
                {
                    owner = await _db.MachineConnectionStates.AsNoTracking()
                        .SingleOrDefaultAsync(state => state.MachineId == message.MachineId, cancellationToken);
                }

                if (owner is null || owner.CompanyId != message.CompanyId || !owner.IsOnline ||
                    owner.ConnectionId != message.ConnectionId || owner.OwnerConnectionId != message.ConnectionId ||
                    owner.OwnerNodeId != message.OwnerNodeId ||
                    owner.CommandRoutingVersion != TelemetryConnectionManager.CurrentCommandRoutingVersion)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return false;
                }
            }

            var idempotencyKey = await CreateIdempotencyKeyAsync(message, cancellationToken);
            if (await _db.DeviceMessageInbox.AnyAsync(row => row.IdempotencyKey == idempotencyKey, cancellationToken))
            {
                await transaction.CommitAsync(cancellationToken);
                return true;
            }

            var pendingCount = await _db.DeviceMessageInbox.CountAsync(
                row => row.Status == "Pending" || row.Status == "Retry" || row.Status == "Processing",
                cancellationToken);
            if (pendingCount >= _maximumPendingMessages)
                throw new DeviceMessageInboxFullException(_maximumPendingMessages);

            var receivedAt = message.ReceivedAt;
            var latestMachineMessageAt = await _db.DeviceMessageInbox
                .Where(row => row.MachineId == message.MachineId &&
                              (row.Status == "Pending" || row.Status == "Retry" || row.Status == "Processing"))
                .Select(row => (DateTime?)row.ReceivedAt)
                .MaxAsync(cancellationToken);
            // The owner row remains locked through insert/commit, so this timestamp
            // preserves queue order even when a reconnect lands on a node with a
            // clock behind an earlier, still-unprocessed message.
            if (latestMachineMessageAt.HasValue && receivedAt <= latestMachineMessageAt.Value)
                receivedAt = latestMachineMessageAt.Value.AddTicks(10);

            _db.DeviceMessageInbox.Add(new DeviceMessageInbox
            {
                IdempotencyKey = idempotencyKey,
                MachineId = message.MachineId,
                CompanyId = message.CompanyId,
                Serial = message.Serial,
                ConnectionId = message.ConnectionId,
                MessageId = message.MessageId,
                DataJson = message.DataJson,
                ReceivedAt = receivedAt,
                Status = "Pending"
            });

            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // A separate API process can still deliver the same event concurrently.
                await transaction.RollbackAsync(cancellationToken);
                _db.ChangeTracker.Clear();
                if (await _db.DeviceMessageInbox.AnyAsync(row => row.IdempotencyKey == idempotencyKey, cancellationToken))
                    return true;
                throw;
            }

            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally
        {
            _admissionGate.Semaphore.Release();
        }
    }

    public async Task<int> RequeueFailedAsync(Guid id, CancellationToken cancellationToken)
    {
        await _admissionGate.Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var transaction = await BeginAdmissionTransactionAsync(cancellationToken);
            var failedMessageExists = await _db.DeviceMessageInbox.AnyAsync(
                row => row.Id == id && row.Status == "Failed", cancellationToken);
            if (!failedMessageExists) return 0;

            var pendingCount = await _db.DeviceMessageInbox.CountAsync(
                row => row.Status == "Pending" || row.Status == "Retry" || row.Status == "Processing",
                cancellationToken);
            if (pendingCount >= _maximumPendingMessages)
                throw new DeviceMessageInboxFullException(_maximumPendingMessages);

            var updated = await _db.DeviceMessageInbox.Where(row => row.Id == id && row.Status == "Failed")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.Status, "Pending")
                    .SetProperty(row => row.Attempts, 0)
                    .SetProperty(row => row.NextAttemptAt, (DateTime?)null)
                    .SetProperty(row => row.ProcessingStartedAt, (DateTime?)null)
                    .SetProperty(row => row.LastError, (string?)null), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return updated;
        }
        finally
        {
            _admissionGate.Semaphore.Release();
        }
    }

    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> BeginAdmissionTransactionAsync(
        CancellationToken cancellationToken)
    {
        var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            if (_db.Database.IsNpgsql())
            {
                // The lock is transaction-scoped and shared across API processes, so
                // count-and-insert remains a single admission decision in a rolling deployment.
                await _db.Database.ExecuteSqlRawAsync(
                    $"SELECT pg_advisory_xact_lock({AdmissionLockNamespace}, {AdmissionLockId})",
                    cancellationToken);
            }

            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    private async Task<string> CreateIdempotencyKeyAsync(InboundDeviceMessage message, CancellationToken cancellationToken)
    {
        var isStatusResponse = false;
        using (var document = JsonDocument.Parse(message.DataJson))
        {
            var root = document.RootElement;
            isStatusResponse = root.ValueKind == JsonValueKind.Object &&
                               root.TryGetProperty("command", out var command) &&
                               command.ValueKind == JsonValueKind.String &&
                               string.Equals(command.GetString(), "status", StringComparison.OrdinalIgnoreCase);
        }

        var activeSessionId = isStatusResponse
            ? null
            : await _db.MachineSessions.AsNoTracking()
                .Where(session => session.MachineId == message.MachineId && session.ClosedAt == null)
                .OrderByDescending(session => session.StartedAt)
                .Select(session => (Guid?)session.Id)
                .FirstOrDefaultAsync(cancellationToken);
        var eventScope = activeSessionId?.ToString("N") ?? message.ConnectionId;
        var source = $"{message.MachineId}\0{eventScope}\0{message.MessageId}\0{message.DataJson}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    }
}

public sealed class DeviceMessageInboxAdmissionGate
{
    public SemaphoreSlim Semaphore { get; } = new(1, 1);
}

public sealed class DeviceMessageInboxFullException(int limit)
    : InvalidOperationException($"A fila durável de telemetria atingiu o limite de {limit} mensagens pendentes.");
