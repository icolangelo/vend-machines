using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;

namespace VendingMachines.Api.Services;

public sealed class OperationalAlertSyncService(AppDbContext db, ILogger<OperationalAlertSyncService> logger)
{
    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var machines = await db.Machines.AsNoTracking()
            .Where(machine => machine.CompanyId.HasValue && machine.IsActive)
            .Select(machine => new { machine.Id, CompanyId = machine.CompanyId!.Value, machine.Name })
            .ToListAsync(cancellationToken);
        var machineIds = machines.Select(machine => machine.Id).ToArray();
        var connections = await db.MachineConnectionStates.AsNoTracking()
            .Where(connection => machineIds.Contains(connection.MachineId) &&
                                 db.Machines.Any(machine => machine.Id == connection.MachineId &&
                                                            machine.CompanyId == connection.CompanyId))
            .ToDictionaryAsync(connection => connection.MachineId, cancellationToken);
        var descriptors = new List<AlertDescriptor>();

        foreach (var machine in machines)
        {
            if (!connections.TryGetValue(machine.Id, out var connection) || !connection.MonitoringEnabled)
            {
                descriptors.Add(new AlertDescriptor(machine.CompanyId, machine.Id, null, null,
                    $"telemetry-unmonitored:{machine.Id}", "TelemetryUnmonitored", "warning",
                    "Máquina sem monitoramento", $"O monitoramento de telemetria de {machine.Name} não está habilitado."));
                continue;
            }

            var lastSeen = connection.LastSeenAt ?? connection.ConnectedAt ?? connection.DisconnectedAt;
            if (connection.IsOnline && connection.OwnerNodeId is null && connection.LastSeenAt >= now.AddSeconds(-90))
            {
                descriptors.Add(new AlertDescriptor(machine.CompanyId, machine.Id, null, null,
                    $"telemetry-owner-unknown:{machine.Id}", "TelemetryOwnerUnknown", "warning",
                    "Roteamento da máquina indisponível",
                    $"{machine.Name} está conectada a uma instância sem identificação de proprietário; comandos e consultas MDB podem retornar indisponível até a reconexão em uma versão compatível."));
            }

            if (lastSeen.HasValue && lastSeen.Value < now.AddMinutes(-5))
            {
                descriptors.Add(new AlertDescriptor(machine.CompanyId, machine.Id, null, null,
                    $"telemetry-offline:{machine.Id}", "TelemetryOffline", "critical",
                    "Máquina sem comunicação", $"{machine.Name} não envia telemetria há mais de 5 minutos. Último contato: {lastSeen.Value:O}."));
            }
            else if (lastSeen.HasValue && lastSeen.Value < now.AddMinutes(-2))
            {
                descriptors.Add(new AlertDescriptor(machine.CompanyId, machine.Id, null, null,
                    $"telemetry-warning:{machine.Id}", "TelemetryWarning", "warning",
                    "Comunicação instável", $"A última comunicação de {machine.Name} ocorreu em {lastSeen.Value:O}."));
            }
        }

        var lowSlots = await db.MachineProductSlots.AsNoTracking()
            .Where(slot => machineIds.Contains(slot.MachineId) &&
                           db.Machines.Any(machine => machine.Id == slot.MachineId && machine.CompanyId == slot.CompanyId) &&
                           slot.CurrentStock <= slot.LowStockThreshold)
            .Select(slot => new { slot.CompanyId, slot.MachineId, slot.ItemNumber, slot.CurrentStock, slot.LowStockThreshold, slot.ProductId })
            .ToListAsync(cancellationToken);
        var machineNames = machines.ToDictionary(machine => machine.Id, machine => machine.Name);
        var productIds = lowSlots.Select(slot => slot.ProductId).Distinct().ToArray();
        var products = await db.Products.AsNoTracking().Where(product => productIds.Contains(product.Id))
            .ToDictionaryAsync(product => product.Id, cancellationToken);
        foreach (var slot in lowSlots)
        {
            machineNames.TryGetValue(slot.MachineId, out var machineName);
            products.TryGetValue(slot.ProductId, out var product);
            var productName = product != null && (product.CompanyId == null || product.CompanyId == slot.CompanyId)
                ? product.Name
                : null;
            descriptors.Add(new AlertDescriptor(slot.CompanyId, slot.MachineId, null, null,
                $"low-stock:{slot.MachineId}:{slot.ItemNumber}", "LowStock", "warning",
                "Compartimento precisa de reposição",
                $"{productName ?? "Produto"} em {machineName ?? slot.MachineId}, compartimento {slot.ItemNumber}: saldo {slot.CurrentStock}, limite {slot.LowStockThreshold}."));
        }

        var failedSessions = await db.MachineSessions.AsNoTracking()
            .Where(session => session.State == MachineSessionStates.DeliveryFailed ||
                              session.State == MachineSessionStates.RefundPending ||
                              session.State == MachineSessionStates.ReconciliationRequired ||
                              session.State == MachineSessionStates.ClosureIncomplete)
            .Where(session => db.Machines.Any(machine => machine.Id == session.MachineId &&
                                                         machine.CompanyId == session.CompanyId))
            .Select(session => new { session.Id, session.CompanyId, session.MachineId, session.TransactionId, session.State, session.LastEventAt })
            .ToListAsync(cancellationToken);
        foreach (var session in failedSessions)
        {
            machineNames.TryGetValue(session.MachineId, out var machineName);
            descriptors.Add(new AlertDescriptor(session.CompanyId, session.MachineId, session.Id, session.TransactionId,
                $"session:{session.Id}", "SessionFailure", "critical", "Sessão precisa de análise",
                $"A sessão da máquina {machineName ?? session.MachineId} está em {session.State} desde {session.LastEventAt:O}."));
        }

        var pendingPayments = await db.PaymentTransactions.AsNoTracking()
            .Where(transaction => transaction.Status == "Pending" && transaction.CreatedAt < now.AddMinutes(-5) &&
                                  transaction.MercadoPagoPaymentId != null && !transaction.MercadoPagoPaymentId.Contains("mock"))
            .Where(transaction => db.Machines.Any(machine => machine.Id == transaction.MachineId &&
                                                             machine.CompanyId == transaction.CompanyId))
            .Select(transaction => new { transaction.Id, transaction.CompanyId, transaction.MachineId, transaction.CreatedAt })
            .ToListAsync(cancellationToken);
        foreach (var payment in pendingPayments)
        {
            machineNames.TryGetValue(payment.MachineId, out var machineName);
            descriptors.Add(new AlertDescriptor(payment.CompanyId, payment.MachineId, null, payment.Id,
                $"payment-pending:{payment.Id}", "PaymentPending", "warning", "Pagamento pendente",
                $"O pagamento na máquina {machineName ?? payment.MachineId} está pendente desde {payment.CreatedAt:O}."));
        }

        var activeAlerts = await db.OperationalAlerts
            .Where(alert => alert.Status == "Active")
            .ToListAsync(cancellationToken);
        var activeByKey = activeAlerts.ToDictionary(alert => (alert.CompanyId, alert.DeduplicationKey));
        var observed = new HashSet<(Guid CompanyId, string Key)>();
        foreach (var descriptor in descriptors)
        {
            var key = (descriptor.CompanyId, descriptor.Key);
            observed.Add(key);
            if (activeByKey.TryGetValue(key, out var existing))
            {
                if (existing.Title != descriptor.Title || existing.Description != descriptor.Description || existing.Severity != descriptor.Severity)
                {
                    existing.Title = descriptor.Title;
                    existing.Description = descriptor.Description;
                    existing.Severity = descriptor.Severity;
                    existing.UpdatedAt = now;
                }
                continue;
            }

            var alert = await db.OperationalAlerts.FirstOrDefaultAsync(
                item => item.CompanyId == descriptor.CompanyId && item.DeduplicationKey == descriptor.Key,
                cancellationToken);
            if (alert is null)
            {
                db.OperationalAlerts.Add(new OperationalAlert
                {
                    CompanyId = descriptor.CompanyId,
                    MachineId = descriptor.MachineId,
                    SessionId = descriptor.SessionId,
                    TransactionId = descriptor.TransactionId,
                    DeduplicationKey = descriptor.Key,
                    Type = descriptor.Type,
                    Severity = descriptor.Severity,
                    Title = descriptor.Title,
                    Description = descriptor.Description,
                    OccurredAt = now,
                    UpdatedAt = now
                });
            }
            else
            {
                alert.MachineId = descriptor.MachineId;
                alert.SessionId = descriptor.SessionId;
                alert.TransactionId = descriptor.TransactionId;
                alert.Type = descriptor.Type;
                alert.Severity = descriptor.Severity;
                alert.Status = "Active";
                alert.Title = descriptor.Title;
                alert.Description = descriptor.Description;
                alert.OccurredAt = now;
                alert.UpdatedAt = now;
                alert.ResolvedAt = null;
                alert.Resolution = null;
                db.AlertReadReceipts.RemoveRange(db.AlertReadReceipts.Where(receipt => receipt.AlertId == alert.Id));
            }
        }

        foreach (var alert in activeAlerts.Where(alert => !observed.Contains((alert.CompanyId, alert.DeduplicationKey))))
        {
            alert.Status = "Resolved";
            alert.ResolvedAt = now;
            alert.UpdatedAt = now;
            alert.Resolution = "A condição que gerou o alerta deixou de ser observada.";
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            logger.LogWarning(exception, "A sincronização concorrente de alertas operacionais será repetida no próximo ciclo.");
        }
    }

    private sealed record AlertDescriptor(Guid CompanyId, string? MachineId, Guid? SessionId, Guid? TransactionId,
        string Key, string Type, string Severity, string Title, string Description);
}
