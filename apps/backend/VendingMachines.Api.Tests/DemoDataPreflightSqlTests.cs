using Npgsql;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class DemoDataPreflightSqlTests
{
    [Fact]
    public async Task PreflightIncludesAllStableDemoUserIdentityMarkers()
    {
        var sql = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "TestData", "demo-data-preflight.sql"));

        Assert.Contains("'22222222-2222-2222-2222-222222222222'", sql, StringComparison.Ordinal);
        Assert.Contains("'33333333-3333-3333-3333-333333333333'", sql, StringComparison.Ordinal);
        Assert.Contains("'dev.ivan@gmail.com'", sql, StringComparison.Ordinal);
        Assert.Contains("'joao@vmmanager.com'", sql, StringComparison.Ordinal);
        Assert.Contains("'DEV.IVAN@GMAIL.COM'", sql, StringComparison.Ordinal);
        Assert.Contains("'JOAO@VMMANAGER.COM'", sql, StringComparison.Ordinal);
        Assert.Contains("'12345678900'", sql, StringComparison.Ordinal);
        Assert.Contains("'98765432199'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadOnlyPreflightCountsKnownSeedMarkersIncludingProductTypesAndUserIdentity()
    {
        var configuredConnection = Environment.GetEnvironmentVariable("VENDMACHINES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configuredConnection))
        {
            if (string.Equals(
                    Environment.GetEnvironmentVariable("VENDMACHINES_REQUIRE_TEST_POSTGRES"),
                    "true",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "O teste de inventário de dados de demonstração exige VENDMACHINES_TEST_POSTGRES no CI.");
            return;
        }

        var schema = $"demo_preflight_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(configuredConnection);
        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var createSchema = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", admin);
            await createSchema.ExecuteNonQueryAsync();
        }

        try
        {
            var scopedBuilder = new NpgsqlConnectionStringBuilder(configuredConnection) { SearchPath = schema };
            await using var connection = new NpgsqlConnection(scopedBuilder.ConnectionString);
            await connection.OpenAsync();
            await using (var setup = new NpgsqlCommand("""
                CREATE TABLE "Companies" ("Id" uuid, "Name" text, "NormalizedCnpj" text);
                CREATE TABLE "Locations" ("Id" uuid);
                CREATE TABLE "Machines" ("Id" text, "SerialNumber" text);
                CREATE TABLE "Products" ("Id" text, "Code" text);
                CREATE TABLE "ProductTypes" ("Id" text);
                CREATE TABLE "ProductPerformances" ("Name" text);
                CREATE TABLE "Users" ("Id" uuid, "Email" text, "NormalizedEmail" text, "NormalizedCpf" text);
                CREATE TABLE "PaymentTransactions" ("MercadoPagoPaymentId" text);

                INSERT INTO "Companies" VALUES
                    ('11111111-1111-1111-1111-111111111111', 'ACME Machines LTDA', '12345678000199'),
                    ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'Empresa comercial', '99887766000155');
                INSERT INTO "Locations" VALUES
                    ('44444444-4444-4444-4444-444444444441'),
                    ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa01');
                INSERT INTO "Machines" VALUES
                    ('VM-001', 'SN-123456'),
                    ('VM-001', 'SERIAL COMERCIAL');
                INSERT INTO "Products" VALUES
                    ('p-1', 'BEB-001'),
                    ('produto-comercial', 'COM-001');
                INSERT INTO "ProductTypes" VALUES ('pt-1'), ('tipo-comercial');
                INSERT INTO "ProductPerformances" VALUES
                    ('Água Mineral 500ml'),
                    ('Produto comercial');
                INSERT INTO "Users" VALUES
                    ('22222222-2222-2222-2222-222222222222', 'dev.ivan@gmail.com', 'DEV.IVAN@GMAIL.COM', '12345678900'),
                    ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa02', 'joao@vmmanager.com', 'OUTRO@EMPRESA.COM', NULL),
                    ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa03', 'usuario@empresa.com', 'USUARIO@EMPRESA.COM', '98765432199'),
                    ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa05', 'usuario.diferente@empresa.com', 'JOAO@VMMANAGER.COM', NULL),
                    ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaa04', 'comercial@empresa.com', 'COMERCIAL@EMPRESA.COM', '11222333000144');
                INSERT INTO "PaymentTransactions" VALUES ('mock_mp_synthetic'), ('mp_commercial_123');
                """, connection))
            {
                await setup.ExecuteNonQueryAsync();
            }

            var sql = await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "TestData", "demo-data-preflight.sql"));
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
                await readOnly.ExecuteNonQueryAsync();

            var results = new Dictionary<string, long>(StringComparer.Ordinal);
            await using (var command = new NpgsqlCommand(sql, connection, transaction))
            await using (var reader = await command.ExecuteReaderAsync())
            {
                Assert.Equal(2, reader.FieldCount);
                Assert.Equal("marker", reader.GetName(0));
                Assert.Equal("candidate_records", reader.GetName(1));
                while (await reader.ReadAsync())
                    results.Add(reader.GetString(0), reader.GetInt64(1));
            }

            Assert.Equal(new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["seed_company_marker"] = 1,
                ["seed_location_marker"] = 1,
                ["seed_machine_marker"] = 1,
                ["seed_product_marker"] = 1,
                ["seed_product_type_marker"] = 1,
                ["seed_product_performance_rows"] = 1,
                ["seed_user_marker"] = 4,
                ["mock_payment_ids"] = 1,
            }, results);

            await transaction.RollbackAsync();
        }
        finally
        {
            await using var cleanup = new NpgsqlConnection(adminBuilder.ConnectionString);
            await cleanup.OpenAsync();
            await using var dropSchema = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", cleanup);
            await dropSchema.ExecuteNonQueryAsync();
        }
    }
}
