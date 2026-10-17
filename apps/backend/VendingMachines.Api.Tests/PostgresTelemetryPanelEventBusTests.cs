using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using System.Threading.Channels;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class PostgresTelemetryPanelEventBusTests
{
    [Fact]
    public async Task LegacyReconnectClearsStaleOwnerAndNewReconnectKeepsMatchingOwnerWhenPostgresIsConfigured()
    {
        var connectionString = Environment.GetEnvironmentVariable("VENDMACHINES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var schema = $"telemetry_owner_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(connectionString);
        await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
        await admin.OpenAsync();
        await using (var createSchema = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin))
            await createSchema.ExecuteNonQueryAsync();

        try
        {
            var scopedBuilder = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema };
            await using (var setup = new NpgsqlConnection(scopedBuilder.ConnectionString))
            {
                await setup.OpenAsync();
                await using var command = new NpgsqlCommand("""
                    CREATE TABLE "MachineConnectionStates" (
                        "MachineId" text PRIMARY KEY,
                        "ConnectionId" text NULL,
                        "OwnerNodeId" character varying(32) NULL,
                        "OwnerConnectionId" character varying(32) NULL,
                        "IsOnline" boolean NOT NULL DEFAULT false
                    );
                    CREATE OR REPLACE FUNCTION clear_legacy_telemetry_owner_on_connection_change()
                    RETURNS trigger
                    LANGUAGE plpgsql
                    AS $$
                    BEGIN
                        IF NEW."ConnectionId" IS DISTINCT FROM OLD."ConnectionId"
                           AND NEW."OwnerConnectionId" IS NOT DISTINCT FROM OLD."OwnerConnectionId" THEN
                            NEW."OwnerNodeId" := NULL;
                            NEW."OwnerConnectionId" := NULL;
                        END IF;
                        RETURN NEW;
                    END;
                    $$;
                    CREATE TRIGGER "TR_MachineConnectionStates_ClearLegacyOwner"
                    BEFORE UPDATE OF "ConnectionId" ON "MachineConnectionStates"
                    FOR EACH ROW
                    WHEN (OLD."ConnectionId" IS DISTINCT FROM NEW."ConnectionId")
                    EXECUTE FUNCTION clear_legacy_telemetry_owner_on_connection_change();
                    """, setup);
                await command.ExecuteNonQueryAsync();
            }

            await using var scoped = new NpgsqlConnection(scopedBuilder.ConnectionString);
            await scoped.OpenAsync();
            await using (var insertLegacy = new NpgsqlCommand("""
                INSERT INTO "MachineConnectionStates" ("MachineId", "ConnectionId", "OwnerNodeId", "OwnerConnectionId", "IsOnline")
                VALUES ('machine-1', 'old-socket', 'node-new', 'old-socket', true);
                UPDATE "MachineConnectionStates" SET "ConnectionId" = 'legacy-socket' WHERE "MachineId" = 'machine-1';
                """, scoped))
                await insertLegacy.ExecuteNonQueryAsync();

            var legacyState = await ReadStateAsync(scoped);
            Assert.True(legacyState.IsOnline);
            Assert.Equal("legacy-socket", legacyState.ConnectionId);
            Assert.Null(legacyState.OwnerNodeId);
            Assert.Null(legacyState.OwnerConnectionId);

            await using (var updateNewOwner = new NpgsqlCommand("""
                UPDATE "MachineConnectionStates"
                SET "ConnectionId" = 'new-socket', "OwnerConnectionId" = 'new-socket', "OwnerNodeId" = 'node-new'
                WHERE "MachineId" = 'machine-1';
                """, scoped))
                await updateNewOwner.ExecuteNonQueryAsync();

            var newState = await ReadStateAsync(scoped);
            Assert.True(newState.IsOnline);
            Assert.Equal("new-socket", newState.ConnectionId);
            Assert.Equal("node-new", newState.OwnerNodeId);
            Assert.Equal("new-socket", newState.OwnerConnectionId);
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", admin);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task PublishAsyncDeliversCompanyScopedNotificationToOtherListenerWhenPostgresIsConfigured()
    {
        var connectionString = Environment.GetEnvironmentVariable("VENDMACHINES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var source = NpgsqlDataSource.Create(connectionString);
        await using var listenerSource = NpgsqlDataSource.Create(connectionString);
        var received = Channel.CreateUnbounded<string>();
        var bus = new PostgresTelemetryPanelEventBus(source, NullLogger<PostgresTelemetryPanelEventBus>.Instance);
        var listenerBus = new PostgresTelemetryPanelEventBus(listenerSource, NullLogger<PostgresTelemetryPanelEventBus>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listenerTask = listenerBus.ListenAsync(payload => received.Writer.TryWrite(payload), timeout.Token);
        await Task.Delay(200, timeout.Token);
        var companyId = Guid.NewGuid();
        await bus.PublishAsync(companyId, "machine-1", "{\"type\":\"sale.updated\"}", "publisher-node", CancellationToken.None);

        var message = JsonSerializer.Deserialize<TelemetryPanelHub.PanelNotification>(
            await received.Reader.ReadAsync(timeout.Token), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(message);
        Assert.Equal(companyId, message.CompanyId);
        Assert.Equal("machine-1", message.MachineId);
        Assert.Equal("publisher-node", message.OriginNodeId);
        Assert.Equal("{\"type\":\"sale.updated\"}", message.PayloadJson);

        await bus.PublishAsync(companyId, "machine-2", JsonSerializer.Serialize(new
        {
            type = "telemetry.received",
            detail = new string('x', 9000)
        }), "publisher-node", CancellationToken.None);
        var oversized = JsonSerializer.Deserialize<TelemetryPanelHub.PanelNotification>(
            await received.Reader.ReadAsync(timeout.Token), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(oversized);
        Assert.Equal("machine-2", oversized.MachineId);
        Assert.Contains("refresh.required", oversized.PayloadJson, StringComparison.Ordinal);
        timeout.Cancel();
        await listenerTask;
    }

    private static async Task<(bool IsOnline, string? ConnectionId, string? OwnerNodeId, string? OwnerConnectionId)> ReadStateAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"IsOnline\", \"ConnectionId\", \"OwnerNodeId\", \"OwnerConnectionId\" FROM \"MachineConnectionStates\" WHERE \"MachineId\" = 'machine-1'";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (
            reader.GetBoolean(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }
}
