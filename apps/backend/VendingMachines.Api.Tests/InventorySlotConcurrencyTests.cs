using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VendingMachines.Api.Controllers;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class InventorySlotConcurrencyTests
{
    [Fact]
    public async Task ConcurrentRestocksPreserveBothQuantitiesAndMovements()
    {
        var configuredConnection = Environment.GetEnvironmentVariable("VENDMACHINES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configuredConnection))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("VENDMACHINES_REQUIRE_TEST_POSTGRES"), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O teste de reposições concorrentes exige VENDMACHINES_TEST_POSTGRES no CI.");
            return;
        }

        var databaseName = $"inventory_restock_race_{Guid.NewGuid():N}";
        var adminConnection = new NpgsqlConnectionStringBuilder(configuredConnection) { Database = "postgres", Pooling = false };
        var testConnection = new NpgsqlConnectionStringBuilder(configuredConnection) { Database = databaseName, Pooling = false };
        await using (var admin = new NpgsqlConnection(adminConnection.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        var companyId = Guid.NewGuid();
        var operatorId = Guid.NewGuid();
        const string machineId = "inventory-restock-race-machine";
        const int itemNumber = 2;
        const string productId = "inventory-restock-race-product";
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(testConnection.ConnectionString)
                .Options;
            await using (var setup = new AppDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                setup.Companies.Add(new Company
                {
                    Id = companyId,
                    Name = "Empresa sintética para reposições",
                    Cnpj = "12345678000195",
                    NormalizedCnpj = "12345678000195",
                    ActivationStatus = CompanyActivationStatuses.Active
                });
                setup.Machines.Add(new Machine
                {
                    Id = machineId,
                    Name = "Máquina sintética",
                    SerialNumber = "SERIAL-INVENTORY-RESTOCK-RACE",
                    NormalizedSerialNumber = "SERIAL-INVENTORY-RESTOCK-RACE",
                    CompanyId = companyId
                });
                setup.Products.Add(new FullProduct
                {
                    Id = productId,
                    Name = "Produto sintético",
                    Code = "INV-RESTOCK-RACE",
                    CompanyId = companyId
                });
                setup.MachineProductSlots.Add(new MachineProductSlot
                {
                    CompanyId = companyId,
                    MachineId = machineId,
                    ItemNumber = itemNumber,
                    ProductId = productId,
                    Capacity = 20,
                    CurrentStock = 2,
                    LowStockThreshold = 2
                });
                await setup.SaveChangesAsync();
            }

            await using var locker = new NpgsqlConnection(testConnection.ConnectionString);
            await locker.OpenAsync();
            await using var lockTransaction = await locker.BeginTransactionAsync();
            await using (var lockRow = new NpgsqlCommand("""
                SELECT "Id" FROM "MachineProductSlots"
                WHERE "CompanyId" = @company_id AND "MachineId" = @machine_id AND "ItemNumber" = @item_number
                FOR UPDATE
                """, locker, lockTransaction))
            {
                lockRow.Parameters.AddWithValue("company_id", companyId);
                lockRow.Parameters.AddWithValue("machine_id", machineId);
                lockRow.Parameters.AddWithValue("item_number", itemNumber);
                Assert.NotNull(await lockRow.ExecuteScalarAsync());
            }

            var firstTask = RestockAsync(5);
            var secondTask = RestockAsync(7);
            var waitDeadline = DateTime.UtcNow.AddSeconds(5);
            var waitingUpdates = 0;
            await using (var observer = new NpgsqlConnection(testConnection.ConnectionString))
            {
                await observer.OpenAsync();
                while (DateTime.UtcNow < waitDeadline && !firstTask.IsCompleted && !secondTask.IsCompleted)
                {
                    await using var waitQuery = new NpgsqlCommand("""
                        SELECT COUNT(*) FROM pg_stat_activity
                        WHERE datname = current_database()
                          AND wait_event_type = 'Lock'
                          AND query ILIKE '%UPDATE "MachineProductSlots"%'
                        """, observer);
                    waitingUpdates = Convert.ToInt32(await waitQuery.ExecuteScalarAsync());
                    if (waitingUpdates >= 1) break;
                    await Task.Delay(TimeSpan.FromMilliseconds(25));
                }
            }

            if (waitingUpdates < 1)
            {
                await lockTransaction.RollbackAsync();
                await Task.WhenAll(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(waitingUpdates >= 1,
                    "Nenhuma reposição chegou à atualização do compartimento enquanto a linha estava bloqueada.");
            }
            Assert.False(firstTask.IsCompleted);
            Assert.False(secondTask.IsCompleted);
            await lockTransaction.CommitAsync();

            var responses = await Task.WhenAll(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(responses, response => Assert.IsType<OkObjectResult>(response));
            await using var verification = new AppDbContext(options);
            var slot = await verification.MachineProductSlots.AsNoTracking().SingleAsync();
            var movements = await verification.InventoryMovements.AsNoTracking().ToListAsync();
            Assert.Equal(14, slot.CurrentStock);
            Assert.Equal(2, movements.Count);
            Assert.Equal(new[] { 5, 7 }, movements.Select(movement => movement.QuantityDelta).Order().ToArray());
            var orderedMovements = movements.OrderBy(movement => movement.QuantityAfter).ToArray();
            Assert.Equal(2 + orderedMovements[0].QuantityDelta, orderedMovements[0].QuantityAfter);
            Assert.Equal(orderedMovements[0].QuantityAfter + orderedMovements[1].QuantityDelta, orderedMovements[1].QuantityAfter);
            Assert.Equal(14, orderedMovements[1].QuantityAfter);

            var retryId = Guid.NewGuid();
            var retries = await Task.WhenAll(RestockAsync(2, retryId), RestockAsync(2, retryId)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(retries, response => Assert.IsType<OkObjectResult>(response));
            await using var retryVerification = new AppDbContext(options);
            Assert.Equal(16, (await retryVerification.MachineProductSlots.AsNoTracking().SingleAsync()).CurrentStock);
            Assert.Equal(3, await retryVerification.InventoryMovements.CountAsync());
            var receiptController = new InventoryController(retryVerification, new InventoryNoOpAuditLogService())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(
                            [new Claim("company_id", companyId.ToString()), new Claim(ClaimTypes.NameIdentifier, operatorId.ToString())], "test"))
                    }
                }
            };
            Assert.IsType<OkObjectResult>(await receiptController.GetMovementByRequest(retryId, CancellationToken.None));

            const string secondMachineId = "inventory-restock-race-machine-2";
            await using (var setupSecond = new AppDbContext(options))
            {
                setupSecond.Machines.Add(new Machine
                {
                    Id = secondMachineId, Name = "Segunda máquina sintética", SerialNumber = "SERIAL-INVENTORY-RESTOCK-RACE-2",
                    NormalizedSerialNumber = "SERIAL-INVENTORY-RESTOCK-RACE-2", CompanyId = companyId
                });
                setupSecond.MachineProductSlots.Add(new MachineProductSlot
                {
                    CompanyId = companyId, MachineId = secondMachineId, ItemNumber = itemNumber,
                    ProductId = productId, Capacity = 20, CurrentStock = 2, LowStockThreshold = 2
                });
                await setupSecond.SaveChangesAsync();
            }

            await using var barrier = new NpgsqlConnection(testConnection.ConnectionString);
            await barrier.OpenAsync();
            await using (var prepareBarrier = new NpgsqlCommand("""
                CREATE FUNCTION inventory_test_insert_barrier() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN PERFORM pg_advisory_xact_lock(738191, 4392); RETURN NEW; END $$;
                CREATE TRIGGER inventory_test_insert_barrier BEFORE INSERT ON "InventoryMovements"
                FOR EACH ROW EXECUTE FUNCTION inventory_test_insert_barrier();
                SELECT pg_advisory_lock(738191, 4392);
                """, barrier))
                await prepareBarrier.ExecuteNonQueryAsync();

            var reusedId = Guid.NewGuid();
            var firstMachine = RestockAsync(1, reusedId);
            var secondMachine = RestockAsync(1, reusedId, secondMachineId);
            var blockedInTrigger = 0;
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                await using var observer = new NpgsqlConnection(testConnection.ConnectionString);
                await observer.OpenAsync();
                while (DateTime.UtcNow < deadline && !firstMachine.IsCompleted && !secondMachine.IsCompleted)
                {
                    await using var waitQuery = new NpgsqlCommand("""
                        SELECT COUNT(*) FROM pg_locks
                        WHERE locktype = 'advisory' AND NOT granted AND classid = 738191 AND objid = 4392
                        """, observer);
                    blockedInTrigger = Convert.ToInt32(await waitQuery.ExecuteScalarAsync());
                    if (blockedInTrigger == 2) break;
                    await Task.Delay(25);
                }
            }
            finally
            {
                await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(738191, 4392)", barrier);
                await release.ExecuteNonQueryAsync();
            }
            var conflictingRequests = await Task.WhenAll(firstMachine, secondMachine).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, blockedInTrigger);
            Assert.Single(conflictingRequests.OfType<OkObjectResult>());
            Assert.Single(conflictingRequests.OfType<ConflictObjectResult>());
            await using var conflictVerification = new AppDbContext(options);
            Assert.Equal(19, await conflictVerification.MachineProductSlots.SumAsync(slot => slot.CurrentStock));
            Assert.Equal(4, await conflictVerification.InventoryMovements.CountAsync());

            async Task<IActionResult> RestockAsync(int quantity, Guid? requestId = null, string? targetMachineId = null)
            {
                await using var db = new AppDbContext(options);
                var controller = new InventoryController(db, new InventoryNoOpAuditLogService())
                {
                    ControllerContext = new ControllerContext
                    {
                        HttpContext = new DefaultHttpContext
                        {
                            User = new ClaimsPrincipal(new ClaimsIdentity(
                                [new Claim("company_id", companyId.ToString()), new Claim(ClaimTypes.NameIdentifier, operatorId.ToString())], "test"))
                        }
                    }
                };
                return await controller.Restock(
                    targetMachineId ?? machineId, itemNumber,
                    new InventoryQuantityRequest(quantity, "Reposição concorrente sintética", requestId),
                    CancellationToken.None);
            }
        }
        finally
        {
            await using var admin = new NpgsqlConnection(adminConnection.ConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task SlotConfigurationCannotLowerCapacityBelowAConcurrentRestock()
    {
        var configuredConnection = Environment.GetEnvironmentVariable("VENDMACHINES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configuredConnection))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("VENDMACHINES_REQUIRE_TEST_POSTGRES"), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O teste concorrente de estoque exige VENDMACHINES_TEST_POSTGRES no CI.");
            return;
        }

        var databaseName = $"inventory_slot_race_{Guid.NewGuid():N}";
        var adminConnection = new NpgsqlConnectionStringBuilder(configuredConnection) { Database = "postgres", Pooling = false };
        var testConnection = new NpgsqlConnectionStringBuilder(configuredConnection) { Database = databaseName, Pooling = false };
        await using (var admin = new NpgsqlConnection(adminConnection.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        var companyId = Guid.NewGuid();
        const string machineId = "inventory-race-machine";
        const int itemNumber = 7;
        const string originalProductId = "inventory-race-original-product";
        const string requestedProductId = "inventory-race-requested-product";
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(testConnection.ConnectionString)
                .Options;
            await using (var setup = new AppDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                setup.Companies.Add(new Company
                {
                    Id = companyId,
                    Name = "Empresa sintética de estoque",
                    Cnpj = "12345678000195",
                    NormalizedCnpj = "12345678000195",
                    ActivationStatus = CompanyActivationStatuses.Active
                });
                setup.Machines.Add(new Machine
                {
                    Id = machineId,
                    Name = "Máquina sintética",
                    SerialNumber = "SERIAL-INVENTORY-RACE",
                    NormalizedSerialNumber = "SERIAL-INVENTORY-RACE",
                    CompanyId = companyId
                });
                setup.Products.AddRange(
                    new FullProduct { Id = originalProductId, Name = "Produto original", Code = "INV-ORIGINAL", CompanyId = companyId },
                    new FullProduct { Id = requestedProductId, Name = "Produto solicitado", Code = "INV-REQUESTED", CompanyId = companyId });
                setup.MachineProductSlots.Add(new MachineProductSlot
                {
                    CompanyId = companyId,
                    MachineId = machineId,
                    ItemNumber = itemNumber,
                    ProductId = originalProductId,
                    Capacity = 100,
                    CurrentStock = 90,
                    LowStockThreshold = 10
                });
                await setup.SaveChangesAsync();
            }

            await using var locker = new NpgsqlConnection(testConnection.ConnectionString);
            await locker.OpenAsync();
            await using var lockTransaction = await locker.BeginTransactionAsync();
            await using (var lockRow = new NpgsqlCommand("""
                SELECT "Id" FROM "MachineProductSlots"
                WHERE "CompanyId" = @company_id AND "MachineId" = @machine_id AND "ItemNumber" = @item_number
                FOR UPDATE
                """, locker, lockTransaction))
            {
                lockRow.Parameters.AddWithValue("company_id", companyId);
                lockRow.Parameters.AddWithValue("machine_id", machineId);
                lockRow.Parameters.AddWithValue("item_number", itemNumber);
                Assert.NotNull(await lockRow.ExecuteScalarAsync());
            }

            await using var controllerDb = new AppDbContext(options);
            var controller = new InventoryController(controllerDb, audit: null!)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(
                            [new Claim("company_id", companyId.ToString())], "test"))
                    }
                }
            };

            var configureTask = controller.ConfigureSlot(
                machineId,
                itemNumber,
                new ConfigureMachineSlotRequest(requestedProductId, Capacity: 95, InitialStock: 90, LowStockThreshold: 5),
                CancellationToken.None);
            var lockWaitDeadline = DateTime.UtcNow.AddSeconds(5);
            var configureWaitsForStockRow = false;
            await using (var observer = new NpgsqlConnection(testConnection.ConnectionString))
            {
                await observer.OpenAsync();
                while (DateTime.UtcNow < lockWaitDeadline && !configureTask.IsCompleted)
                {
                    await using var waitQuery = new NpgsqlCommand("""
                        SELECT EXISTS (
                            SELECT 1 FROM pg_stat_activity
                            WHERE datname = current_database()
                              AND wait_event_type = 'Lock'
                              AND query ILIKE '%MachineProductSlots%'
                        )
                        """, observer);
                    configureWaitsForStockRow = await waitQuery.ExecuteScalarAsync() as bool? == true;
                    if (configureWaitsForStockRow) break;
                    await Task.Delay(TimeSpan.FromMilliseconds(25));
                }
            }

            if (!configureWaitsForStockRow)
            {
                await lockTransaction.RollbackAsync();
                var unexpectedResult = await configureTask.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(configureWaitsForStockRow,
                    $"A configuração terminou sem aguardar o lock da linha; resultado: {unexpectedResult.GetType().Name}.");
            }

            await using (var restock = new NpgsqlCommand("""
                UPDATE "MachineProductSlots"
                SET "CurrentStock" = 100
                WHERE "CompanyId" = @company_id AND "MachineId" = @machine_id AND "ItemNumber" = @item_number
                """, locker, lockTransaction))
            {
                restock.Parameters.AddWithValue("company_id", companyId);
                restock.Parameters.AddWithValue("machine_id", machineId);
                restock.Parameters.AddWithValue("item_number", itemNumber);
                Assert.Equal(1, await restock.ExecuteNonQueryAsync());
            }
            await lockTransaction.CommitAsync();

            var result = await configureTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsType<ConflictObjectResult>(result);
            await using (var verify = new AppDbContext(options))
            {
                var slot = await verify.MachineProductSlots.AsNoTracking().SingleAsync();
                Assert.Equal(100, slot.CurrentStock);
                Assert.Equal(100, slot.Capacity);
                Assert.Equal(originalProductId, slot.ProductId);
                Assert.Empty(await verify.InventoryMovements.ToListAsync());
            }
        }
        finally
        {
            await using var admin = new NpgsqlConnection(adminConnection.ConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class InventoryNoOpAuditLogService : IAuditLogService
    {
        public Task RecordAsync(
            string action,
            string resourceType,
            string? resourceId = null,
            Guid? companyId = null,
            object? metadata = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
