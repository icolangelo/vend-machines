using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class SessionOrchestratorFlowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulDelivery_CompletesAndClosesSession(bool includeTransactionId)
    {
        await using var scenario = await TestScenario.CreatePaidAsync(MachineSessionStates.PaymentApproved);

        await scenario.SendAsync("pool", "vend_approved", includeTransactionId);
        await scenario.SendAsync("vend", "success", includeTransactionId);

        var delivered = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.Completed, delivered.State);
        Assert.Null(delivered.DeliveryDeadlineAt);
        Assert.Equal("Approved", delivered.Transaction!.Status);
        Assert.Equal(1, await scenario.EventCountAsync("delivery.success"));
        Assert.Equal(0, await scenario.EventCountAsync("refund.requested"));

        await scenario.CloseFromDeviceAsync(includeTransactionId);

        var closed = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.Completed, closed.State);
        Assert.NotNull(closed.ClosedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeliveryFailure_RefundsAndClosesSession(bool includeTransactionId)
    {
        await using var scenario = await TestScenario.CreatePaidAsync(MachineSessionStates.AwaitingDeliveryResult);

        await scenario.SendAsync("vend", "failure", includeTransactionId, reason: "mechanism_error");

        var failed = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.Refunded, failed.State);
        Assert.Equal("Refunded", failed.Transaction!.Status);
        Assert.Equal(1, await scenario.EventCountAsync("delivery.failure"));
        Assert.Equal(1, await scenario.EventCountAsync("refund.requested"));
        Assert.Equal(1, await scenario.EventCountAsync("refund.completed"));

        var deliveryFailure = await scenario.Db.DeliveryFailures.AsNoTracking().SingleAsync();
        Assert.Equal("mechanism_error", deliveryFailure.Reason);
        Assert.Equal("mechanism_error", deliveryFailure.OriginalReason);

        await scenario.CloseFromDeviceAsync(includeTransactionId);
        Assert.NotNull((await scenario.ReloadSessionAsync()).ClosedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeliveryFailureWithoutReason_UsesUnknownAndRefunds(bool includeTransactionId)
    {
        await using var scenario = await TestScenario.CreatePaidAsync(MachineSessionStates.AwaitingDeliveryResult);

        await scenario.SendAsync("vend", "failure", includeTransactionId);

        var session = await scenario.ReloadSessionAsync();
        var deliveryFailure = await scenario.Db.DeliveryFailures.AsNoTracking().SingleAsync();
        var refundRequested = await scenario.SingleEventAsync("refund.requested");

        Assert.Equal(MachineSessionStates.Refunded, session.State);
        Assert.Equal("unknown", deliveryFailure.Reason);
        Assert.Equal("unknown", deliveryFailure.OriginalReason);
        Assert.Equal("delivery_failure:unknown", refundRequested.Detail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedPayment_EndsAsDenied(bool includeTransactionId)
    {
        await using var scenario = await TestScenario.CreatePaidAsync(
            MachineSessionStates.PaymentPending,
            transactionStatus: "Pending");

        await scenario.Orchestrator.HandlePaymentResultAsync(
            scenario.TransactionId,
            approved: false,
            "mercado_pago_webhook",
            CancellationToken.None);
        await scenario.SendAsync("pool", "vend_denied", includeTransactionId);
        await scenario.CloseFromDeviceAsync(includeTransactionId);

        var session = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.Denied, session.State);
        Assert.Equal("Rejected", session.Transaction!.Status);
        Assert.NotNull(session.ClosedAt);
        Assert.Equal(1, await scenario.EventCountAsync("payment.denied"));
        Assert.Equal(0, await scenario.EventCountAsync("refund.requested"));
    }

    [Fact]
    public async Task DuplicatePaymentApproval_RecordsDuplicateWithoutSendingSecondCommandOrCreatingSale()
    {
        await using var scenario = await TestScenario.CreatePaidAsync(
            MachineSessionStates.PaymentPending,
            transactionStatus: "Pending");

        await scenario.Orchestrator.HandlePaymentResultAsync(
            scenario.TransactionId, approved: true, "mercado_pago_webhook", CancellationToken.None);
        await scenario.Orchestrator.HandlePaymentResultAsync(
            scenario.TransactionId, approved: true, "mercado_pago_webhook", CancellationToken.None);

        var session = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.Refunded, session.State);
        Assert.Equal("Refunded", session.Transaction!.Status);
        Assert.Equal(1, await scenario.EventCountAsync("payment.approved"));
        Assert.Equal(1, await scenario.EventCountAsync("payment.duplicate_result"));
        Assert.Equal(1, await scenario.Db.TelemetryCommands.AsNoTracking()
            .CountAsync(command => command.SessionId == scenario.SessionId && command.Command == "VENDA_APROVADA"));
        Assert.Equal(1, await scenario.EventCountAsync("refund.requested"));
        Assert.Equal(1, await scenario.OutboxCountAsync("refund_payment"));
        Assert.Empty(await scenario.Db.SaleRecords.AsNoTracking()
            .Where(sale => sale.TransactionId == scenario.TransactionId).ToListAsync());
    }

    [Fact]
    public async Task ProductionSessionRejectsMockProviderCredentialsInsteadOfCreatingMockPix()
    {
        await using var scenario = await TestScenario.CreateWithoutTransactionAsync(
            MachineSessionStates.AwaitingSelection,
            environmentName: Environments.Production,
            addMockIntegration: true);

        await scenario.SendRawAsync(new
        {
            command = "vend",
            type = "request",
            itemPrice = 200,
            itemNumber = 1
        });

        var transaction = await scenario.Db.PaymentTransactions.SingleAsync();
        Assert.Equal("Failed", transaction.Status);
        Assert.Null(transaction.MercadoPagoPaymentId);
        Assert.Empty(transaction.QrCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellingPendingPix_CancelsPaymentAndAllowsClosure(bool includeTransactionId)
    {
        await using var scenario = await TestScenario.CreatePaidAsync(
            MachineSessionStates.PaymentPending,
            transactionStatus: "Pending");

        await scenario.Orchestrator.CancelSessionAsync(
            scenario.SessionId,
            scenario.CompanyId,
            Guid.NewGuid(),
            "user_cancelled",
            CancellationToken.None);
        await scenario.SendAsync("pool", "vend_denied", includeTransactionId);
        await scenario.CloseFromDeviceAsync(includeTransactionId);

        var pending = await scenario.ReloadSessionAsync();
        Assert.Equal("CancellationPending", pending.Transaction!.Status);
        await scenario.Orchestrator.ProcessOutboxAsync(CancellationToken.None);

        var session = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.Denied, session.State);
        Assert.Equal("Cancelled", session.Transaction!.Status);
        Assert.NotNull(session.Transaction.CompletedAt);
        Assert.Equal("user_cancelled", session.CloseReason);
        Assert.NotNull(session.ClosedAt);
        Assert.Equal(1, await scenario.EventCountAsync("payment.cancelled"));
        Assert.Equal(1, await scenario.OutboxCountAsync("cancel_payment"));
    }

    [Fact]
    public async Task ClosingWithoutSelection_DoesNotRequireTransactionId()
    {
        await using var scenario = await TestScenario.CreateWithoutTransactionAsync(MachineSessionStates.AwaitingSelection);

        await scenario.SendAsync("vend", "sessioncomplete", includeTransactionId: false);
        await scenario.SendAsync("pool", "end_session", includeTransactionId: false);

        var session = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.ClosedByDeviceBeforeSelection, session.State);
        Assert.Equal("device_closed_before_selection", session.CloseReason);
        Assert.NotNull(session.ClosedAt);
        Assert.Equal(0, await scenario.EventCountAsync("refund.requested"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EndSessionBeforeDeliveryResult_ClosesAndRefunds(bool includeTransactionId)
    {
        await using var scenario = await TestScenario.CreatePaidAsync(MachineSessionStates.AwaitingDeliveryResult);

        await scenario.SendAsync("pool", "end_session", includeTransactionId);

        var session = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.Refunded, session.State);
        Assert.Equal("Refunded", session.Transaction!.Status);
        Assert.Equal("delivery_result_missing", session.CloseReason);
        Assert.NotNull(session.ClosedAt);
        Assert.Equal(1, await scenario.EventCountAsync("delivery.result_missing"));
        Assert.Equal(1, await scenario.EventCountAsync("refund.requested"));
        Assert.Equal(1, await scenario.EventCountAsync("refund.completed"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EndSession_ProviderFailureKeepsPhysicalSessionClosedAndRefundPending(bool includeTransactionId)
    {
        await using var scenario = await TestScenario.CreatePaidAsync(MachineSessionStates.AwaitingDeliveryResult);
        await scenario.SetProviderPaymentIdAsync("external-payment-without-integration");

        await scenario.SendAsync("pool", "end_session", includeTransactionId);

        var session = await scenario.ReloadSessionAsync();
        var outbox = await scenario.SingleOutboxAsync("refund_payment");
        Assert.NotNull(session.ClosedAt);
        Assert.Equal(MachineSessionStates.RefundPending, session.State);
        Assert.Equal("RefundPending", session.Transaction!.Status);
        Assert.Equal("pending", outbox.Status);
        Assert.Equal(1, outbox.Attempts);
        Assert.Equal(1, await scenario.EventCountAsync("refund.requested"));
        Assert.Equal(0, await scenario.EventCountAsync("refund.completed"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosedSession_FinancialOutboxCanResumeAfterProviderRecovery(bool includeTransactionId)
    {
        await using var scenario = await TestScenario.CreatePaidAsync(MachineSessionStates.AwaitingDeliveryResult);
        await scenario.SetProviderPaymentIdAsync("external-payment-without-integration");
        await scenario.SendAsync("pool", "end_session", includeTransactionId);
        var physicallyClosedAt = (await scenario.ReloadSessionAsync()).ClosedAt;

        await scenario.SetProviderPaymentIdAsync($"mock_{Guid.NewGuid():N}");
        await scenario.MakeOutboxDueAsync("refund_payment");
        await scenario.Orchestrator.ProcessOutboxAsync(CancellationToken.None);

        var session = await scenario.ReloadSessionAsync();
        var outbox = await scenario.SingleOutboxAsync("refund_payment");
        Assert.Equal(physicallyClosedAt, session.ClosedAt);
        Assert.Equal(MachineSessionStates.Refunded, session.State);
        Assert.Equal("Refunded", session.Transaction!.Status);
        Assert.Equal("processed", outbox.Status);
        Assert.Equal(2, outbox.Attempts);
        Assert.Equal(1, await scenario.EventCountAsync("refund.completed"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EndSessionDuringPendingPayment_ClosesBeforeCancellationIsProcessed(bool includeTransactionId)
    {
        await using var scenario = await TestScenario.CreatePaidAsync(
            MachineSessionStates.PaymentPending,
            transactionStatus: "Pending");
        await scenario.SetProviderPaymentIdAsync("external-payment-without-integration");

        await scenario.SendAsync("pool", "end_session", includeTransactionId);

        var session = await scenario.ReloadSessionAsync();
        var outbox = await scenario.SingleOutboxAsync("cancel_payment");
        Assert.NotNull(session.ClosedAt);
        Assert.Equal(MachineSessionStates.Denied, session.State);
        Assert.Equal("CancellationPending", session.Transaction!.Status);
        Assert.Equal("pending", outbox.Status);
        Assert.Equal(0, outbox.Attempts);

        await scenario.Orchestrator.ProcessOutboxAsync(CancellationToken.None);
        session = await scenario.ReloadSessionAsync();
        outbox = await scenario.SingleOutboxAsync("cancel_payment");
        Assert.NotNull(session.ClosedAt);
        Assert.Equal("CancellationPending", session.Transaction!.Status);
        Assert.Equal(1, outbox.Attempts);
    }

    [Fact]
    public async Task RefundFailureAfterFiveAttempts_RequiresReconciliationButStaysClosed()
    {
        await using var scenario = await TestScenario.CreatePaidAsync(MachineSessionStates.AwaitingDeliveryResult);
        await scenario.SetProviderPaymentIdAsync("external-payment-without-integration");
        await scenario.SendAsync("pool", "end_session", includeTransactionId: true);

        for (var attempt = 1; attempt < 5; attempt++)
        {
            await scenario.MakeOutboxDueAsync("refund_payment");
            await scenario.Orchestrator.ProcessOutboxAsync(CancellationToken.None);
        }

        var session = await scenario.ReloadSessionAsync();
        var outbox = await scenario.SingleOutboxAsync("refund_payment");
        Assert.NotNull(session.ClosedAt);
        Assert.Equal(MachineSessionStates.ReconciliationRequired, session.State);
        Assert.Equal("ReconciliationRequired", session.Transaction!.Status);
        Assert.Equal("failed", outbox.Status);
        Assert.Equal(5, outbox.Attempts);
        Assert.Equal(1, await scenario.EventCountAsync("refund.failed"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateEndSession_DoesNotDuplicateFinancialWork(bool includeTransactionId)
    {
        await using var scenario = await TestScenario.CreatePaidAsync(MachineSessionStates.AwaitingDeliveryResult);

        await scenario.SendAsync("pool", "end_session", includeTransactionId);
        await scenario.SendAsync("pool", "end_session", includeTransactionId);

        Assert.NotNull((await scenario.ReloadSessionAsync()).ClosedAt);
        Assert.Equal(1, await scenario.OutboxCountAsync("refund_payment"));
        Assert.Equal(1, await scenario.EventCountAsync("refund.requested"));
        Assert.Equal(1, await scenario.EventCountAsync("refund.completed"));
    }

    [Theory]
    [InlineData("vend", "failure")]
    [InlineData("pool", "end_session")]
    public async Task DivergentTransactionId_IsRejectedWithoutChangingState(string command, string type)
    {
        await using var scenario = await TestScenario.CreatePaidAsync(MachineSessionStates.AwaitingDeliveryResult);

        await scenario.SendAsync(
            command,
            type,
            includeTransactionId: true,
            reason: "mechanism_error",
            transactionIdOverride: Guid.NewGuid().ToString());

        var session = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.AwaitingDeliveryResult, session.State);
        Assert.NotNull(session.DeliveryDeadlineAt);
        Assert.Equal("Approved", session.Transaction!.Status);
        Assert.Equal(1, await scenario.EventCountAsync("protocol.transaction_mismatch"));
        Assert.Equal(0, await scenario.EventCountAsync("refund.requested"));
    }

    [Theory]
    [InlineData("vend", "failure")]
    [InlineData("pool", "end_session")]
    public async Task InvalidTransactionId_IsRejectedWithoutChangingState(string command, string type)
    {
        await using var scenario = await TestScenario.CreatePaidAsync(MachineSessionStates.AwaitingDeliveryResult);

        await scenario.SendAsync(
            command,
            type,
            includeTransactionId: true,
            reason: "mechanism_error",
            transactionIdOverride: "transaction-id-invalido");

        var session = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.AwaitingDeliveryResult, session.State);
        Assert.Equal("Approved", session.Transaction!.Status);
        Assert.Equal(1, await scenario.EventCountAsync("protocol.invalid_transaction_id"));
        Assert.Equal(0, await scenario.EventCountAsync("refund.requested"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateFailure_IsIdempotent(bool includeTransactionId)
    {
        await using var scenario = await TestScenario.CreatePaidAsync(MachineSessionStates.AwaitingDeliveryResult);

        await scenario.SendAsync("vend", "failure", includeTransactionId, reason: "out_of_product");
        await scenario.SendAsync("vend", "failure", includeTransactionId, reason: "out_of_product");

        var session = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.Refunded, session.State);
        Assert.Equal(1, await scenario.Db.DeliveryFailures.AsNoTracking().CountAsync());
        Assert.Equal(1, await scenario.EventCountAsync("refund.requested"));
        Assert.Equal(1, await scenario.EventCountAsync("refund.completed"));
        Assert.Equal(1, await scenario.OutboxCountAsync("refund_payment"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateSuccess_IsIdempotent(bool includeTransactionId)
    {
        await using var scenario = await TestScenario.CreatePaidAsync(MachineSessionStates.AwaitingDeliveryResult);

        await scenario.SendAsync("vend", "success", includeTransactionId);
        await scenario.SendAsync("vend", "success", includeTransactionId);

        var session = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.Completed, session.State);
        Assert.Equal("Approved", session.Transaction!.Status);
        Assert.Equal(1, await scenario.EventCountAsync("delivery.success"));
        Assert.Equal(0, await scenario.EventCountAsync("refund.requested"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessAfterRefund_IsRecordedAsConflict(bool includeTransactionId)
    {
        await using var scenario = await TestScenario.CreatePaidAsync(MachineSessionStates.AwaitingDeliveryResult);

        await scenario.SendAsync("vend", "failure", includeTransactionId, reason: "out_of_cup");
        await scenario.SendAsync("vend", "success", includeTransactionId);

        var session = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.Refunded, session.State);
        Assert.Equal("Refunded", session.Transaction!.Status);
        Assert.Equal(1, await scenario.EventCountAsync("delivery.conflicting_result"));
        Assert.Equal(1, await scenario.EventCountAsync("refund.requested"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureAfterSuccess_IsRecordedAsConflictWithoutRefund(bool includeTransactionId)
    {
        await using var scenario = await TestScenario.CreatePaidAsync(MachineSessionStates.AwaitingDeliveryResult);

        await scenario.SendAsync("vend", "success", includeTransactionId);
        await scenario.SendAsync("vend", "failure", includeTransactionId, reason: "mechanism_error");

        var session = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.Completed, session.State);
        Assert.Equal("Approved", session.Transaction!.Status);
        Assert.Equal(1, await scenario.EventCountAsync("delivery.conflicting_result"));
        Assert.Equal(0, await scenario.EventCountAsync("refund.requested"));
        Assert.Equal(0, await scenario.Db.DeliveryFailures.AsNoTracking().CountAsync());
    }

    [Theory]
    [InlineData(MdbStatuses.Inactive)]
    [InlineData(MdbStatuses.Disabled)]
    [InlineData(MdbStatuses.Enabled)]
    [InlineData(MdbStatuses.Idle)]
    [InlineData(MdbStatuses.Vend)]
    public async Task MdbStatusWithoutDataType_IsPersistedWithoutChangingActiveSession(string mdbStatus)
    {
        await using var scenario = await TestScenario.CreateWithoutTransactionAsync(MachineSessionStates.AwaitingSelection);

        await scenario.SendRawAsync(new { command = "status", status = mdbStatus });

        scenario.Db.ChangeTracker.Clear();
        var connection = await scenario.Db.MachineConnectionStates.AsNoTracking()
            .SingleAsync(x => x.MachineId == scenario.MachineId);
        var session = await scenario.ReloadSessionAsync();

        Assert.Equal(mdbStatus, connection.MdbStatus);
        Assert.NotNull(connection.MdbStatusUpdatedAt);
        Assert.Contains(mdbStatus, connection.MdbStatusRaw);
        Assert.Equal(MachineSessionStates.AwaitingSelection, session.State);
        Assert.Equal(1, await scenario.Db.TelemetryEvents.AsNoTracking()
            .CountAsync(x => x.MachineId == scenario.MachineId && x.EventType == "mdb.status.changed"));
    }

    [Fact]
    public async Task UnknownMdbStatus_BecomesUnavailableAndDoesNotOpenOrCloseSession()
    {
        await using var scenario = await TestScenario.CreateWithoutTransactionAsync(MachineSessionStates.AwaitingSelection);

        await scenario.SendRawAsync(new { command = "status", status = "unexpected_state" });
        await scenario.SendRawAsync(new { command = "status", status = "unexpected_state" });

        scenario.Db.ChangeTracker.Clear();
        var connection = await scenario.Db.MachineConnectionStates.AsNoTracking()
            .SingleAsync(x => x.MachineId == scenario.MachineId);
        var session = await scenario.ReloadSessionAsync();

        Assert.Null(connection.MdbStatus);
        Assert.NotNull(connection.MdbStatusUpdatedAt);
        Assert.Equal(MachineSessionStates.AwaitingSelection, session.State);
        Assert.Equal(1, await scenario.Db.TelemetryEvents.AsNoTracking()
            .CountAsync(x => x.MachineId == scenario.MachineId && x.EventType == "mdb.status.invalid"));
    }

    [Fact]
    public async Task SuccessfulDelivery_RecordsOneSaleAndDecrementsMappedStockOnce()
    {
        await using var scenario = await TestScenario.CreatePaidAsync(MachineSessionStates.AwaitingDeliveryResult);
        scenario.Db.ProductTypes.Add(new ProductType
        {
            Id = "drinks",
            Name = "Bebidas",
            CompanyId = scenario.CompanyId
        });
        scenario.Db.Products.Add(new FullProduct
        {
            Id = "water-500ml",
            Code = "W500",
            Name = "Água 500 ml",
            TypeId = "drinks",
            CompanyId = scenario.CompanyId,
            Cost = 1.25m
        });
        scenario.Db.MachineProductSlots.Add(new MachineProductSlot
        {
            CompanyId = scenario.CompanyId,
            MachineId = scenario.MachineId,
            ItemNumber = 2,
            ProductId = "water-500ml",
            Capacity = 10,
            CurrentStock = 3,
            LowStockThreshold = 2
        });
        await scenario.Db.SaveChangesAsync();

        await scenario.SendAsync("vend", "success", includeTransactionId: true);
        await scenario.SendAsync("vend", "success", includeTransactionId: true);

        var sale = await scenario.Db.SaleRecords.AsNoTracking().SingleAsync();
        var slot = await scenario.Db.MachineProductSlots.AsNoTracking().SingleAsync();
        var movement = await scenario.Db.InventoryMovements.AsNoTracking().SingleAsync();
        Assert.Equal(scenario.TransactionId, sale.TransactionId);
        Assert.Equal("water-500ml", sale.ProductId);
        Assert.Equal("Água 500 ml", sale.ProductNameSnapshot);
        Assert.Equal(2m, sale.GrossAmount);
        Assert.Equal(1, sale.Quantity);
        Assert.Equal(2, slot.CurrentStock);
        Assert.Equal(-1, movement.QuantityDelta);
        Assert.Equal(2, movement.QuantityAfter);
    }

    [Fact]
    public async Task SuccessfulDeliveryDoesNotSnapshotProductNameFromAnotherCompany()
    {
        await using var scenario = await TestScenario.CreatePaidAsync(MachineSessionStates.AwaitingDeliveryResult);
        var foreignCompanyId = Guid.NewGuid();
        scenario.Db.Companies.Add(new Company
        {
            Id = foreignCompanyId,
            Name = "Empresa estrangeira",
            Cnpj = "22222222000122"
        });
        scenario.Db.ProductTypes.Add(new ProductType
        {
            Id = "foreign-drinks",
            Name = "Bebidas estrangeiras",
            CompanyId = foreignCompanyId
        });
        scenario.Db.Products.Add(new FullProduct
        {
            Id = "foreign-water",
            Code = "FW500",
            Name = "Produto confidencial de outra empresa",
            TypeId = "foreign-drinks",
            CompanyId = foreignCompanyId
        });
        scenario.Db.MachineProductSlots.Add(new MachineProductSlot
        {
            CompanyId = scenario.CompanyId,
            MachineId = scenario.MachineId,
            ItemNumber = 2,
            ProductId = "foreign-water",
            Capacity = 10,
            CurrentStock = 3,
            LowStockThreshold = 2
        });
        await scenario.Db.SaveChangesAsync();

        await scenario.SendAsync("vend", "success", includeTransactionId: true);

        var sale = await scenario.Db.SaleRecords.AsNoTracking().SingleAsync();
        Assert.Null(sale.ProductNameSnapshot);
        Assert.Equal("foreign-water", sale.ProductId);
    }

    [Fact]
    public async Task DeviceMessageFromNewMachineOwnerDoesNotAdvancePreviousOwnersSession()
    {
        await using var scenario = await TestScenario.CreatePaidAsync(MachineSessionStates.AwaitingDeliveryResult);
        var newCompanyId = Guid.NewGuid();
        scenario.Db.Companies.Add(new Company
        {
            Id = newCompanyId,
            Name = "Nova empresa proprietária",
            Cnpj = "33333333000133"
        });
        var machine = await scenario.Db.Machines.SingleAsync(item => item.Id == scenario.MachineId);
        machine.CompanyId = newCompanyId;
        await scenario.Db.SaveChangesAsync();

        var data = JsonSerializer.Serialize(new
        {
            command = "vend",
            type = "success",
            transactionId = scenario.TransactionId
        });
        await scenario.Orchestrator.HandleDeviceMessageAsync(
            new InboundDeviceMessage(
                scenario.MachineId,
                newCompanyId,
                machine.SerialNumber,
                "new-owner-connection",
                991,
                data,
                DateTime.UtcNow),
            CancellationToken.None);

        var session = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.AwaitingDeliveryResult, session.State);
        Assert.Empty(await scenario.Db.SaleRecords.AsNoTracking().ToListAsync());
        Assert.Contains(await scenario.Db.TelemetryEvents.AsNoTracking().ToListAsync(), item =>
            item.CompanyId == newCompanyId && item.MachineId == scenario.MachineId && item.SessionId == null);
    }

    [Fact]
    public async Task OutboxWithSessionFromAnotherCompanyDoesNotCallProviderOrChangeEitherSession()
    {
        await using var scenario = await TestScenario.CreatePaidAsync(MachineSessionStates.AwaitingDeliveryResult);
        await scenario.SetProviderPaymentIdAsync("external-payment-for-outbox-test");
        scenario.Db.MercadoPagoIntegrations.Add(new MercadoPagoIntegration
        {
            CompanyId = scenario.CompanyId,
            IsActive = true,
            AccessToken = EncryptionService.Encrypt("test-access-token"),
            RefreshToken = string.Empty,
            ClientSecret = string.Empty
        });
        await scenario.Db.SaveChangesAsync();

        var foreignCompanyId = Guid.NewGuid();
        var foreignMachine = new Machine
        {
            Id = "foreign-outbox-machine",
            Name = "Máquina de outra empresa",
            SerialNumber = "FOREIGN-OUTBOX-SERIAL",
            CompanyId = foreignCompanyId
        };
        var foreignSession = new MachineSession
        {
            Id = Guid.NewGuid(),
            MachineId = foreignMachine.Id,
            CompanyId = foreignCompanyId,
            State = MachineSessionStates.Completed,
            ClosedAt = DateTime.UtcNow
        };
        scenario.Db.Companies.Add(new Company
        {
            Id = foreignCompanyId,
            Name = "Empresa estrangeira",
            Cnpj = "44444444000144"
        });
        scenario.Db.Machines.Add(foreignMachine);
        scenario.Db.MachineSessions.Add(foreignSession);
        var outbox = new OutboxMessage
        {
            CompanyId = scenario.CompanyId,
            MachineId = scenario.MachineId,
            SessionId = foreignSession.Id,
            TransactionId = scenario.TransactionId,
            MessageType = "refund_payment",
            Status = "pending",
            NextAttemptAt = DateTime.UtcNow.AddMinutes(-1)
        };
        scenario.Db.OutboxMessages.Add(outbox);
        await scenario.Db.SaveChangesAsync();
        var outboxId = outbox.Id;

        await scenario.Orchestrator.ProcessOutboxAsync(CancellationToken.None);

        outbox = await scenario.Db.OutboxMessages.AsNoTracking().SingleAsync(message => message.Id == outboxId);
        var originalSession = await scenario.ReloadSessionAsync();
        var unchangedForeignSession = await scenario.Db.MachineSessions.AsNoTracking()
            .SingleAsync(session => session.Id == foreignSession.Id);
        Assert.Equal("failed", outbox.Status);
        Assert.Equal("association_mismatch", outbox.LastError);
        Assert.Equal(0, outbox.Attempts);
        Assert.Equal(0, scenario.ProviderRequestCount);
        Assert.Equal(MachineSessionStates.AwaitingDeliveryResult, originalSession.State);
        Assert.Equal("Approved", originalSession.Transaction!.Status);
        Assert.Equal(MachineSessionStates.Completed, unchangedForeignSession.State);
        Assert.DoesNotContain(await scenario.Db.TelemetryEvents.AsNoTracking().ToListAsync(),
            item => item.SessionId == foreignSession.Id && item.EventType == "refund.completed");
    }

    [Fact]
    public async Task Database_AllowsOnlyOneOpenSessionPerMachine()
    {
        await using var scenario = await TestScenario.CreateWithoutTransactionAsync(MachineSessionStates.AwaitingSelection);
        scenario.Db.MachineSessions.Add(new MachineSession
        {
            MachineId = scenario.MachineId,
            CompanyId = scenario.CompanyId,
            State = MachineSessionStates.Opening
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => scenario.Db.SaveChangesAsync());
    }

    private sealed class TestScenario : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly TestHttpClientFactory _httpClientFactory;
        private int _messageId;

        private TestScenario(
            SqliteConnection connection,
            AppDbContext db,
            SessionOrchestrator orchestrator,
            Guid companyId,
            string machineId,
            Guid sessionId,
            Guid? transactionId,
            TestHttpClientFactory httpClientFactory)
        {
            _connection = connection;
            Db = db;
            Orchestrator = orchestrator;
            CompanyId = companyId;
            MachineId = machineId;
            SessionId = sessionId;
            TransactionId = transactionId ?? Guid.Empty;
            _httpClientFactory = httpClientFactory;
        }

        public AppDbContext Db { get; }
        public SessionOrchestrator Orchestrator { get; }
        public Guid CompanyId { get; }
        public string MachineId { get; }
        public Guid SessionId { get; }
        public Guid TransactionId { get; }
        public int ProviderRequestCount => _httpClientFactory.RequestCount;

        public static Task<TestScenario> CreatePaidAsync(string state, string transactionStatus = "Approved") =>
            CreateAsync(state, includeTransaction: true, transactionStatus);

        public static Task<TestScenario> CreateWithoutTransactionAsync(
            string state,
            string? environmentName = null,
            bool addMockIntegration = false) =>
            CreateAsync(
                state,
                includeTransaction: false,
                transactionStatus: "Pending",
                environmentName: environmentName ?? Environments.Development,
                addMockIntegration: addMockIntegration);

        private static async Task<TestScenario> CreateAsync(
            string state,
            bool includeTransaction,
            string transactionStatus,
            string? environmentName = null,
            bool addMockIntegration = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var company = new Company
            {
                Id = Guid.NewGuid(),
                Name = "Empresa de teste",
                Cnpj = "00000000000000",
                CreatedByUserId = Guid.NewGuid()
            };
            var machine = new Machine
            {
                Id = $"machine-{Guid.NewGuid():N}",
                Name = "Máquina de teste",
                SerialNumber = $"SERIAL-{Guid.NewGuid():N}",
                NormalizedSerialNumber = $"SERIAL-{Guid.NewGuid():N}".ToUpperInvariant(),
                CompanyId = company.Id,
                MercadoPagoEnabled = true
            };

            PaymentTransaction? transaction = null;
            if (includeTransaction)
            {
                transaction = new PaymentTransaction
                {
                    Id = Guid.NewGuid(),
                    MachineId = machine.Id,
                    CompanyId = company.Id,
                    Amount = 2m,
                    Status = transactionStatus,
                    MercadoPagoPaymentId = $"mock_{Guid.NewGuid():N}"
                };
            }

            var session = new MachineSession
            {
                Id = Guid.NewGuid(),
                MachineId = machine.Id,
                CompanyId = company.Id,
                State = state,
                ItemNumber = includeTransaction ? 2 : null,
                AmountCents = includeTransaction ? 200 : null,
                TransactionId = transaction?.Id,
                SelectionDeadlineAt = state == MachineSessionStates.AwaitingSelection
                    ? DateTime.UtcNow.AddMinutes(2)
                    : null,
                PaymentDeadlineAt = state == MachineSessionStates.PaymentPending
                    ? DateTime.UtcNow.AddMinutes(5)
                    : null,
                DeliveryDeadlineAt = state is MachineSessionStates.PaymentApproved or MachineSessionStates.AwaitingDeliveryResult
                    ? DateTime.UtcNow.AddMinutes(1)
                    : null
            };

            db.Companies.Add(company);
            db.Machines.Add(machine);
            if (addMockIntegration)
            {
                db.MercadoPagoIntegrations.Add(new MercadoPagoIntegration
                {
                    CompanyId = company.Id,
                    IsActive = true,
                    AccessToken = "mock_access_token",
                    RefreshToken = string.Empty,
                    ClientSecret = string.Empty
                });
            }
            if (transaction != null) db.PaymentTransactions.Add(transaction);
            db.MachineSessions.Add(session);
            await db.SaveChangesAsync();

            var panelHub = new TelemetryPanelHub(NullLogger<TelemetryPanelHub>.Instance);
            var connectionManager = new TelemetryConnectionManager(
                new UnusedScopeFactory(),
                panelHub,
                NullLogger<TelemetryConnectionManager>.Instance);
            var httpClientFactory = new TestHttpClientFactory();
            var orchestrator = new SessionOrchestrator(
                db,
                connectionManager,
                panelHub,
                httpClientFactory,
                new ConfigurationBuilder().Build(),
                new TestWebHostEnvironment(environmentName ?? Environments.Development),
                NullLogger<SessionOrchestrator>.Instance);

            return new TestScenario(
                connection,
                db,
                orchestrator,
                company.Id,
                machine.Id,
                session.Id,
                transaction?.Id,
                httpClientFactory);
        }

        public async Task SendAsync(
            string command,
            string type,
            bool includeTransactionId,
            string? reason = null,
            string? transactionIdOverride = null)
        {
            var data = new Dictionary<string, object?>
            {
                ["command"] = command,
                ["type"] = type
            };
            if (includeTransactionId)
                data["transactionId"] = transactionIdOverride ?? TransactionId.ToString();
            if (reason != null)
                data["reason"] = reason;

            var messageId = Interlocked.Increment(ref _messageId);
            await Orchestrator.HandleDeviceMessageAsync(
                new InboundDeviceMessage(
                    MachineId,
                    CompanyId,
                    "SERIAL-TEST",
                    "connection-test",
                    messageId,
                    JsonSerializer.Serialize(data),
                    DateTime.UtcNow.AddTicks(messageId)),
                CancellationToken.None);
        }

        public async Task SendRawAsync(object data)
        {
            var messageId = Interlocked.Increment(ref _messageId);
            await Orchestrator.HandleDeviceMessageAsync(
                new InboundDeviceMessage(
                    MachineId,
                    CompanyId,
                    "SERIAL-TEST",
                    "connection-test",
                    messageId,
                    JsonSerializer.Serialize(data),
                    DateTime.UtcNow.AddTicks(messageId)),
                CancellationToken.None);
        }

        public async Task CloseFromDeviceAsync(bool includeTransactionId)
        {
            await SendAsync("vend", "sessioncomplete", includeTransactionId);
            await SendAsync("pool", "end_session", includeTransactionId);
        }

        public async Task<MachineSession> ReloadSessionAsync()
        {
            Db.ChangeTracker.Clear();
            return await Db.MachineSessions
                .AsNoTracking()
                .Include(x => x.Transaction)
                .SingleAsync(x => x.Id == SessionId);
        }

        public Task<int> EventCountAsync(string eventType) =>
            Db.TelemetryEvents.AsNoTracking().CountAsync(x => x.SessionId == SessionId && x.EventType == eventType);

        public Task<TelemetryEvent> SingleEventAsync(string eventType) =>
            Db.TelemetryEvents.AsNoTracking().SingleAsync(x => x.SessionId == SessionId && x.EventType == eventType);

        public Task<int> OutboxCountAsync(string messageType) =>
            Db.OutboxMessages.AsNoTracking().CountAsync(x => x.SessionId == SessionId && x.MessageType == messageType);

        public Task<OutboxMessage> SingleOutboxAsync(string messageType) =>
            Db.OutboxMessages.AsNoTracking().SingleAsync(x => x.SessionId == SessionId && x.MessageType == messageType);

        public async Task SetProviderPaymentIdAsync(string paymentId)
        {
            var transaction = await Db.PaymentTransactions.SingleAsync(x => x.Id == TransactionId);
            transaction.MercadoPagoPaymentId = paymentId;
            await Db.SaveChangesAsync();
        }

        public async Task MakeOutboxDueAsync(string messageType)
        {
            var outbox = await Db.OutboxMessages.SingleAsync(
                x => x.SessionId == SessionId && x.MessageType == messageType);
            outbox.NextAttemptAt = DateTime.UtcNow.AddSeconds(-1);
            await Db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class UnusedScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("Escopo não utilizado nos testes.");
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public HttpClient CreateClient(string name) => new(new RejectExternalRequestsHandler(
            () => Interlocked.Increment(ref _requestCount)));
    }

    private sealed class RejectExternalRequestsHandler(Action onRequest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onRequest();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    private sealed class TestWebHostEnvironment(string environmentName) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "VendingMachines.Api.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = environmentName;
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
