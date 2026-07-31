using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Models;

namespace VendingMachines.Api.Data;

public static class DbInitializer
{
    public static void Initialize(AppDbContext context, bool seedDemoData)
    {
        // Executar migrations automáticas se necessário ou apenas garantir a criação para SQLite
        if (context.Database.IsSqlite())
        {
            context.Database.EnsureCreated();
            EnsureSqliteCompatibilitySchema(context);
        }
        else
        {
            context.Database.Migrate();
        }

        CleanUpEmptyMachineIds(context);

        if (!seedDemoData)
        {
            return;
        }

        // Definição de IDs fixos para garantir relacionamentos consistentes
        var companyId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var adminUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var joaoUserId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var shoppingLocationId = Guid.Parse("44444444-4444-4444-4444-444444444441");
        var hospitalLocationId = Guid.Parse("44444444-4444-4444-4444-444444444442");

        // 0. Seed de SystemSettings
        if (!context.SystemSettings.Any())
        {
            var defaultSettings = new SystemSettings
            {
                Id = Guid.NewGuid(),
                ApplicationFeePercent = 5.0m,
                UpdatedAt = DateTime.UtcNow
            };
            context.SystemSettings.Add(defaultSettings);
        }

        // 1. Seed de Companies
        if (!context.Companies.Any())
        {
            var defaultCompany = new Company
            {
                Id = companyId,
                Name = "ACME Machines LTDA",
                CreatedByUserId = adminUserId,
                CreatedAt = DateTime.UtcNow,
                Cnpj = "12.345.678/0001-99"
            };
            context.Companies.Add(defaultCompany);
        }

        // 1.1 Seed de Localizações padrão da empresa
        if (!context.Locations.Any())
        {
            context.Locations.AddRange(
                new Location { Id = shoppingLocationId, CompanyId = companyId, Name = "ACME - Shopping", CreatedAt = DateTime.UtcNow },
                new Location { Id = hospitalLocationId, CompanyId = companyId, Name = "ACME - Hospital", CreatedAt = DateTime.UtcNow }
            );
        }

        // 2. Seed de ProductTypes
        if (!context.ProductTypes.Any())
        {
            var productTypes = new List<ProductType>
            {
                new() { Id = "pt-1", Name = "Bebidas Frias" },
                new() { Id = "pt-2", Name = "Bebidas Quentes" },
                new() { Id = "pt-3", Name = "Carne" },
                new() { Id = "pt-4", Name = "Doces" },
                new() { Id = "pt-5", Name = "Outros" },
                new() { Id = "pt-6", Name = "Refeição" },
                new() { Id = "pt-7", Name = "Sanduíche" },
                new() { Id = "pt-8", Name = "Snacks" },
                new() { Id = "pt-9", Name = "Yogurt" }
            };
            context.ProductTypes.AddRange(productTypes);
        }

        // 3. Seed de Machines
        if (!context.Machines.Any())
        {
            var machines = new List<Machine>
            {
                new() { Id = "VM-001", Name = "Lobby A1", ClientName = "ACME - Hospital", Location = "Lobby Principal", Status = "online", StockLevel = 82, Revenue30d = 4230m, TotalSales30d = 847, LastSync = "2 min atrás", SerialNumber = "SN-123456", CompanyId = companyId, LocationId = hospitalLocationId },
                new() { Id = "VM-002", Name = "Refeitório B2", ClientName = "ACME - Hospital", Location = "Refeitório 2º andar", Status = "warning", StockLevel = 18, Revenue30d = 3890m, TotalSales30d = 778, LastSync = "5 min atrás", SerialNumber = "SN-987654", CompanyId = companyId, LocationId = hospitalLocationId },
                new() { Id = "VM-003", Name = "UTI C1", ClientName = "ACME - Hospital", Location = "Corredor UTI", Status = "online", StockLevel = 65, Revenue30d = 2150m, TotalSales30d = 430, LastSync = "1 min atrás", SerialNumber = "SN-555555", CompanyId = companyId, LocationId = hospitalLocationId },
                new() { Id = "VM-004", Name = "Recepção D1", ClientName = "ACME - Shopping", Location = "Bloco D", Status = "online", StockLevel = 91, Revenue30d = 5670m, TotalSales30d = 1134, LastSync = "3 min atrás", SerialNumber = "SN-004", CompanyId = companyId, LocationId = shoppingLocationId },
                new() { Id = "VM-005", Name = "Cantina E1", ClientName = "ACME - Shopping", Location = "Cantina Central", Status = "offline", StockLevel = 0, Revenue30d = 0m, TotalSales30d = 0, LastSync = "2 dias atrás", SerialNumber = "SN-005", CompanyId = companyId, LocationId = shoppingLocationId },
                new() { Id = "VM-006", Name = "Hall F1", ClientName = "ACME - Shopping", Location = "Hall de Entrada", Status = "online", StockLevel = 45, Revenue30d = 1890m, TotalSales30d = 378, LastSync = "4 min atrás", SerialNumber = "SN-006", CompanyId = companyId, LocationId = shoppingLocationId },
                new() { Id = "VM-007", Name = "Academia G1", ClientName = "ACME - Shopping", Location = "Academia", Status = "warning", StockLevel = 12, Revenue30d = 3420m, TotalSales30d = 684, LastSync = "8 min atrás", SerialNumber = "SN-007", CompanyId = companyId, LocationId = shoppingLocationId },
                new() { Id = "VM-008", Name = "Terminal H1", ClientName = "ACME - Shopping", Location = "Terminal 3", Status = "online", StockLevel = 73, Revenue30d = 8940m, TotalSales30d = 1788, LastSync = "1 min atrás", SerialNumber = "SN-008", CompanyId = companyId, LocationId = shoppingLocationId },
                new() { Id = "VM-009", Name = "Embarque I1", ClientName = "ACME - Shopping", Location = "Sala de Embarque", Status = "online", StockLevel = 56, Revenue30d = 7230m, TotalSales30d = 1446, LastSync = "2 min atrás", SerialNumber = "SN-009", CompanyId = companyId, LocationId = shoppingLocationId },
                new() { Id = "VM-010", Name = "Plataforma J1", ClientName = "ACME - Shopping", Location = "Plataforma 12", Status = "warning", StockLevel = 22, Revenue30d = 6180m, TotalSales30d = 1236, LastSync = "15 min atrás", SerialNumber = "SN-010", CompanyId = companyId, LocationId = shoppingLocationId },
                new() { Id = "VM-011", Name = "Escritório K1", ClientName = "ACME - Shopping", Location = "12º andar", Status = "online", StockLevel = 88, Revenue30d = 4560m, TotalSales30d = 912, LastSync = "1 min atrás", SerialNumber = "SN-011", CompanyId = companyId, LocationId = shoppingLocationId },
                new() { Id = "VM-012", Name = "Lounge L1", ClientName = "ACME - Shopping", Location = "Lounge Café", Status = "online", StockLevel = 71, Revenue30d = 5230m, TotalSales30d = 1046, LastSync = "3 min atrás", SerialNumber = "SN-012", CompanyId = companyId, LocationId = shoppingLocationId }
            };
            context.Machines.AddRange(machines);
        }
        else
        {
            var defaultCompany = context.Companies.FirstOrDefault();
            if (defaultCompany != null)
            {
                var unlinkedMachines = context.Machines.Where(m => m.CompanyId == null).ToList();
                if (unlinkedMachines.Any())
                {
                    var fallbackLocation = context.Locations.FirstOrDefault(l => l.CompanyId == defaultCompany.Id);
                    foreach (var m in unlinkedMachines)
                    {
                        m.CompanyId = defaultCompany.Id;
                        if (m.LocationId == null && fallbackLocation != null)
                        {
                            m.LocationId = fallbackLocation.Id;
                            m.ClientName = fallbackLocation.Name;
                        }
                    }
                    context.SaveChanges();
                }
            }
        }

        // 4. Seed de Products (FullProduct)
        if (!context.Products.Any())
        {
            var products = new List<FullProduct>
            {
                new() { Id = "p-1", Code = "BEB-001", Name = "Coca-Cola Lata", Description = "Refrigerante de cola 350ml", TypeId = "pt-1", IsAlcoholic = false, Cost = 2.500m },
                new() { Id = "p-2", Code = "ALC-001", Name = "Cerveja Heineken", Description = "Cerveja lager premium 330ml", TypeId = "pt-1", IsAlcoholic = true, Cost = 4.850m },
                new() { Id = "p-3", Code = "SNA-001", Name = "Batata Pringles", Description = "Batata frita sabor original 114g", TypeId = "pt-8", IsAlcoholic = false, Cost = 12.300m }
            };
            context.Products.AddRange(products);
        }

        // 5. Seed de ProductPerformances
        if (!context.ProductPerformances.Any())
        {
            var performances = new List<ProductPerformance>
            {
                // Top Products
                new() { Name = "Água Mineral 500ml", TotalSold = 3240, Revenue = 9720m, Trend = 12.3, IsTop = true },
                new() { Name = "Coca-Cola Lata", TotalSold = 2890, Revenue = 14450m, Trend = 8.1, IsTop = true },
                new() { Name = "Barra de Cereal", TotalSold = 1670, Revenue = 5845m, Trend = 5.4, IsTop = true },
                new() { Name = "Suco Del Valle", TotalSold = 1420, Revenue = 7100m, Trend = -2.1, IsTop = true },
                new() { Name = "Biscoito Oreo", TotalSold = 1180, Revenue = 4720m, Trend = 15.7, IsTop = true },
                
                // Bottom Products
                new() { Name = "Chá Gelado Leão", TotalSold = 89, Revenue = 356m, Trend = -28.4, IsTop = false },
                new() { Name = "Amendoim Japonês", TotalSold = 112, Revenue = 336m, Trend = -19.2, IsTop = false },
                new() { Name = "Energético Monster", TotalSold = 156, Revenue = 1248m, Trend = -12.8, IsTop = false },
                new() { Name = "Paçoca Amor", TotalSold = 178, Revenue = 356m, Trend = -8.5, IsTop = false },
                new() { Name = "Vitamina Yakult", TotalSold = 203, Revenue = 609m, Trend = -5.1, IsTop = false }
            };
            context.ProductPerformances.AddRange(performances);
        }

        // 6. Seed de Users (Dono/Criador e Sócio)
        if (!context.Users.Any())
        {
            var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<User>();
            
            var adminUser = new User
            {
                Id = adminUserId,
                Name = "Admin Ivan",
                Email = "dev.ivan@gmail.com",
                Role = "Admin",
                CompanyId = companyId,
                CreatedAt = DateTime.UtcNow,
                Cpf = "123.456.789-00",
                AcceptedPrivacyPolicy = true,
                PrivacyPolicyAcceptedAt = DateTime.UtcNow
            };
            adminUser.PasswordHash = hasher.HashPassword(adminUser, "admin123");
            context.Users.Add(adminUser);

            var partnerUser = new User
            {
                Id = joaoUserId,
                Name = "João da Silva",
                Email = "joao@vmmanager.com",
                Role = "Admin",
                CompanyId = companyId,
                CreatedAt = DateTime.UtcNow,
                Cpf = "987.654.321-99",
                AcceptedPrivacyPolicy = true,
                PrivacyPolicyAcceptedAt = DateTime.UtcNow
            };
            partnerUser.PasswordHash = hasher.HashPassword(partnerUser, "teste123");
            context.Users.Add(partnerUser);
        }

        foreach (var entry in context.ChangeTracker.Entries<Machine>())
        {
            var serial = entry.Entity.SerialNumber?.Trim().ToUpperInvariant();
            entry.Entity.NormalizedSerialNumber = string.IsNullOrWhiteSpace(serial) ? null : serial;
        }

        context.SaveChanges();
    }

    private static void EnsureSqliteCompatibilitySchema(AppDbContext context)
    {
        context.Database.ExecuteSqlRaw("""
CREATE TABLE IF NOT EXISTS "Locations" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Locations" PRIMARY KEY,
    "CompanyId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "UpdatedAt" TEXT NULL,
    CONSTRAINT "FK_Locations_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies" ("Id") ON DELETE CASCADE
);
""");

        context.Database.ExecuteSqlRaw("""
CREATE UNIQUE INDEX IF NOT EXISTS "IX_Locations_CompanyId_Name" ON "Locations" ("CompanyId", "Name");
""");

        if (!SqliteColumnExists(context, "Machines", "LocationId"))
        {
            context.Database.ExecuteSqlRaw("""
ALTER TABLE "Machines" ADD COLUMN "LocationId" TEXT NULL;
""");
        }

        EnsureSqliteColumn(context, "MercadoPagoIntegrations", "RefreshToken", "TEXT NOT NULL DEFAULT ''");
        EnsureSqliteColumn(context, "MercadoPagoIntegrations", "AccessTokenExpiresAt", "TEXT NULL");
        EnsureSqliteColumn(context, "MercadoPagoIntegrations", "MercadoPagoUserId", "TEXT NOT NULL DEFAULT ''");
        EnsureSqliteColumn(context, "MercadoPagoIntegrations", "MercadoPagoNickname", "TEXT NOT NULL DEFAULT ''");
        EnsureSqliteColumn(context, "MercadoPagoIntegrations", "MercadoPagoSiteId", "TEXT NOT NULL DEFAULT ''");
        EnsureSqliteColumn(context, "MercadoPagoIntegrations", "LastTokenValidationAt", "TEXT NULL");
        EnsureSqliteColumn(context, "MercadoPagoIntegrations", "LastTokenValidationStatus", "TEXT NOT NULL DEFAULT ''");

        EnsureSqliteColumn(context, "Products", "CompanyId", "TEXT NULL");
        EnsureSqliteColumn(context, "Products", "OriginalId", "TEXT NULL");
        EnsureSqliteColumn(context, "ProductTypes", "CompanyId", "TEXT NULL");
        EnsureSqliteColumn(context, "ProductTypes", "OriginalId", "TEXT NULL");
        EnsureSqliteColumn(context, "PaymentTransactions", "SendTelemetryToMachine", "INTEGER NOT NULL DEFAULT 1");
        EnsureSqliteColumn(context, "Machines", "NormalizedSerialNumber", "TEXT NULL");
        EnsureSqliteColumn(context, "Machines", "AutoOpenSessionEnabled", "INTEGER NOT NULL DEFAULT 0");

        context.Database.ExecuteSqlRaw("""
UPDATE "Machines" SET "NormalizedSerialNumber" = upper(trim("SerialNumber"))
WHERE "SerialNumber" IS NOT NULL AND trim("SerialNumber") <> '';
CREATE UNIQUE INDEX IF NOT EXISTS "IX_Machines_NormalizedSerialNumber"
ON "Machines" ("NormalizedSerialNumber");
""");

        context.Database.ExecuteSqlRaw("""
CREATE TABLE IF NOT EXISTS "MachineConnectionStates" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_MachineConnectionStates" PRIMARY KEY,
    "MachineId" TEXT NOT NULL,
    "CompanyId" TEXT NOT NULL,
    "MonitoringEnabled" INTEGER NOT NULL DEFAULT 0,
    "IsOnline" INTEGER NOT NULL DEFAULT 0,
    "ConnectionId" TEXT NULL,
    "ConnectedAt" TEXT NULL,
    "DisconnectedAt" TEXT NULL,
    "LastSeenAt" TEXT NULL,
    "ActivatedAt" TEXT NULL,
    "ActivatedByUserId" TEXT NULL,
    "DeactivatedAt" TEXT NULL,
    "DeactivatedByUserId" TEXT NULL,
    "MdbStatus" TEXT NULL,
    "MdbStatusRaw" TEXT NULL,
    "MdbStatusUpdatedAt" TEXT NULL,
    "MdbStatusRequestAt" TEXT NULL,
    "MdbStatusRequestMessageId" INTEGER NULL,
    "AutoOpenLastAttemptAt" TEXT NULL,
    "AutoOpenLastError" TEXT NULL,
    CONSTRAINT "FK_MachineConnectionStates_Machines_MachineId" FOREIGN KEY ("MachineId") REFERENCES "Machines" ("Id") ON DELETE CASCADE
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_MachineConnectionStates_MachineId" ON "MachineConnectionStates" ("MachineId");
CREATE INDEX IF NOT EXISTS "IX_MachineConnectionStates_CompanyId_IsOnline" ON "MachineConnectionStates" ("CompanyId", "IsOnline");
""");

        EnsureSqliteColumn(context, "MachineConnectionStates", "MdbStatus", "TEXT NULL");
        EnsureSqliteColumn(context, "MachineConnectionStates", "MdbStatusRaw", "TEXT NULL");
        EnsureSqliteColumn(context, "MachineConnectionStates", "MdbStatusUpdatedAt", "TEXT NULL");
        EnsureSqliteColumn(context, "MachineConnectionStates", "MdbStatusRequestAt", "TEXT NULL");
        EnsureSqliteColumn(context, "MachineConnectionStates", "MdbStatusRequestMessageId", "INTEGER NULL");
        EnsureSqliteColumn(context, "MachineConnectionStates", "AutoOpenLastAttemptAt", "TEXT NULL");
        EnsureSqliteColumn(context, "MachineConnectionStates", "AutoOpenLastError", "TEXT NULL");

        context.Database.ExecuteSqlRaw("""
CREATE TABLE IF NOT EXISTS "MachineSessions" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_MachineSessions" PRIMARY KEY,
    "MachineId" TEXT NOT NULL,
    "CompanyId" TEXT NOT NULL,
    "StartedByUserId" TEXT NULL,
    "Source" TEXT NOT NULL,
    "State" TEXT NOT NULL,
    "CloseReason" TEXT NULL,
    "ItemNumber" INTEGER NULL,
    "AmountCents" INTEGER NULL,
    "TransactionId" TEXT NULL,
    "StartedAt" TEXT NOT NULL,
    "LastEventAt" TEXT NOT NULL,
    "SelectionDeadlineAt" TEXT NULL,
    "PaymentDeadlineAt" TEXT NULL,
    "DeliveryDeadlineAt" TEXT NULL,
    "ClosedAt" TEXT NULL,
    CONSTRAINT "FK_MachineSessions_Machines_MachineId" FOREIGN KEY ("MachineId") REFERENCES "Machines" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_MachineSessions_PaymentTransactions_TransactionId" FOREIGN KEY ("TransactionId") REFERENCES "PaymentTransactions" ("Id") ON DELETE SET NULL
);
CREATE INDEX IF NOT EXISTS "IX_MachineSessions_CompanyId_MachineId_State" ON "MachineSessions" ("CompanyId", "MachineId", "State");
CREATE INDEX IF NOT EXISTS "IX_MachineSessions_MachineId" ON "MachineSessions" ("MachineId");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_MachineSessions_TransactionId" ON "MachineSessions" ("TransactionId");
""");

        EnsureNoDuplicateOpenSessions(context);
        context.Database.ExecuteSqlRaw("""
CREATE UNIQUE INDEX IF NOT EXISTS "UX_MachineSessions_OneOpenPerMachine"
ON "MachineSessions" ("MachineId")
WHERE "ClosedAt" IS NULL;
""");

        context.Database.ExecuteSqlRaw("""
CREATE TABLE IF NOT EXISTS "TelemetryCommands" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_TelemetryCommands" PRIMARY KEY,
    "CompanyId" TEXT NOT NULL, "MachineId" TEXT NOT NULL, "SessionId" TEXT NULL,
    "TransactionId" TEXT NULL, "UserId" TEXT NULL, "MessageId" INTEGER NOT NULL,
    "Direction" TEXT NOT NULL, "Command" TEXT NOT NULL, "DataJson" TEXT NULL,
    "Status" TEXT NOT NULL, "Attempts" INTEGER NOT NULL, "CreatedAt" TEXT NOT NULL,
    "SentAt" TEXT NULL, "AckAt" TEXT NULL, "Error" TEXT NULL
);
CREATE INDEX IF NOT EXISTS "IX_TelemetryCommands_CompanyId_MachineId_CreatedAt" ON "TelemetryCommands" ("CompanyId", "MachineId", "CreatedAt");

CREATE TABLE IF NOT EXISTS "TelemetryEvents" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_TelemetryEvents" PRIMARY KEY,
    "CompanyId" TEXT NOT NULL, "MachineId" TEXT NOT NULL, "SessionId" TEXT NULL,
    "TransactionId" TEXT NULL, "UserId" TEXT NULL, "EventType" TEXT NOT NULL,
    "Detail" TEXT NULL, "DataJson" TEXT NULL, "CreatedAt" TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_TelemetryEvents_CompanyId_MachineId_CreatedAt" ON "TelemetryEvents" ("CompanyId", "MachineId", "CreatedAt");
""");

        context.Database.ExecuteSqlRaw("""
CREATE TABLE IF NOT EXISTS "DeliveryFailures" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_DeliveryFailures" PRIMARY KEY,
    "CompanyId" TEXT NOT NULL, "MachineId" TEXT NOT NULL, "SessionId" TEXT NOT NULL,
    "TransactionId" TEXT NOT NULL, "Reason" TEXT NOT NULL, "OriginalReason" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_DeliveryFailures_TransactionId" ON "DeliveryFailures" ("TransactionId");

CREATE TABLE IF NOT EXISTS "OutboxMessages" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_OutboxMessages" PRIMARY KEY,
    "CompanyId" TEXT NOT NULL, "MachineId" TEXT NOT NULL, "SessionId" TEXT NULL,
    "TransactionId" TEXT NULL, "MessageType" TEXT NOT NULL, "PayloadJson" TEXT NOT NULL,
    "Status" TEXT NOT NULL, "Attempts" INTEGER NOT NULL, "CreatedAt" TEXT NOT NULL,
    "NextAttemptAt" TEXT NULL, "ProcessedAt" TEXT NULL, "LastError" TEXT NULL
);
CREATE INDEX IF NOT EXISTS "IX_OutboxMessages_Status_NextAttemptAt" ON "OutboxMessages" ("Status", "NextAttemptAt");
""");

        context.Database.ExecuteSqlRaw("""
CREATE INDEX IF NOT EXISTS "IX_Machines_LocationId" ON "Machines" ("LocationId");
""");

        context.Database.ExecuteSqlRaw("""
INSERT INTO "Locations" ("Id", "CompanyId", "Name", "CreatedAt")
SELECT upper(hex(randomblob(4))) || '-' || upper(hex(randomblob(2))) || '-' || upper(hex(randomblob(2))) || '-' || upper(hex(randomblob(2))) || '-' || upper(hex(randomblob(6))),
       c."Id",
       COALESCE(NULLIF(substr(trim(c."Name"), 1, instr(trim(c."Name") || ' ', ' ') - 1), ''), 'Empresa') || ' - Shopping',
       CURRENT_TIMESTAMP
FROM "Companies" c
WHERE NOT EXISTS (
    SELECT 1 FROM "Locations" l
    WHERE l."CompanyId" = c."Id"
      AND l."Name" = COALESCE(NULLIF(substr(trim(c."Name"), 1, instr(trim(c."Name") || ' ', ' ') - 1), ''), 'Empresa') || ' - Shopping'
);
""");

        context.Database.ExecuteSqlRaw("""
INSERT INTO "Locations" ("Id", "CompanyId", "Name", "CreatedAt")
SELECT upper(hex(randomblob(4))) || '-' || upper(hex(randomblob(2))) || '-' || upper(hex(randomblob(2))) || '-' || upper(hex(randomblob(2))) || '-' || upper(hex(randomblob(6))),
       c."Id",
       COALESCE(NULLIF(substr(trim(c."Name"), 1, instr(trim(c."Name") || ' ', ' ') - 1), ''), 'Empresa') || ' - Hospital',
       CURRENT_TIMESTAMP
FROM "Companies" c
WHERE NOT EXISTS (
    SELECT 1 FROM "Locations" l
    WHERE l."CompanyId" = c."Id"
      AND l."Name" = COALESCE(NULLIF(substr(trim(c."Name"), 1, instr(trim(c."Name") || ' ', ' ') - 1), ''), 'Empresa') || ' - Hospital'
);
""");

        // Fix existing lowercase Guids
        context.Database.ExecuteSqlRaw("""
UPDATE "Locations" SET "Id" = upper("Id") WHERE "Id" != upper("Id");
UPDATE "Machines" SET "LocationId" = upper("LocationId") WHERE "LocationId" != upper("LocationId");
""");

        context.Database.ExecuteSqlRaw("""
UPDATE "Machines"
SET "LocationId" = (
        SELECT l."Id"
        FROM "Locations" l
        WHERE l."CompanyId" = "Machines"."CompanyId"
          AND l."Name" = trim("Machines"."ClientName")
        LIMIT 1
    )
WHERE "LocationId" IS NULL
  AND EXISTS (
      SELECT 1 FROM "Locations" l
      WHERE l."CompanyId" = "Machines"."CompanyId"
        AND l."Name" = trim("Machines"."ClientName")
  );
""");

        context.Database.ExecuteSqlRaw("""
UPDATE "Machines"
SET "LocationId" = (
        SELECT l."Id"
        FROM "Locations" l
        WHERE l."CompanyId" = "Machines"."CompanyId"
        ORDER BY l."CreatedAt", l."Name"
        LIMIT 1
    ),
    "ClientName" = (
        SELECT l."Name"
        FROM "Locations" l
        WHERE l."CompanyId" = "Machines"."CompanyId"
        ORDER BY l."CreatedAt", l."Name"
        LIMIT 1
    )
WHERE "LocationId" IS NULL
  AND EXISTS (
      SELECT 1 FROM "Locations" l
      WHERE l."CompanyId" = "Machines"."CompanyId"
  );
""");
    }

    private static void EnsureSqliteColumn(AppDbContext context, string tableName, string columnName, string columnDefinition)
    {
        if (SqliteColumnExists(context, tableName, columnName))
        {
            return;
        }

        var sql = $"""
ALTER TABLE "{tableName}" ADD COLUMN "{columnName}" {columnDefinition};
""";
        context.Database.ExecuteSqlRaw(sql);
    }

    private static bool SqliteColumnExists(AppDbContext context, string tableName, string columnName)
    {
        var connection = context.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
        {
            connection.Open();
        }

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{tableName}\");";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            if (shouldClose)
            {
                connection.Close();
            }
        }
    }

    private static void EnsureNoDuplicateOpenSessions(AppDbContext context)
    {
        var connection = context.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
        {
            connection.Open();
        }

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
SELECT "MachineId"
FROM "MachineSessions"
WHERE "ClosedAt" IS NULL
GROUP BY "MachineId"
HAVING COUNT(*) > 1
LIMIT 1;
""";
            var duplicateMachineId = command.ExecuteScalar()?.ToString();
            if (!string.IsNullOrWhiteSpace(duplicateMachineId))
            {
                throw new InvalidOperationException(
                    $"A máquina {duplicateMachineId} possui mais de uma sessão aberta. Corrija os dados antes de iniciar a aplicação.");
            }
        }
        finally
        {
            if (shouldClose)
            {
                connection.Close();
            }
        }
    }

    private static void CleanUpEmptyMachineIds(AppDbContext context)
    {
        var connection = context.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
        {
            connection.Open();
        }

        try
        {
            var machinesToFix = new List<(string OldId, string SerialNumber)>();
            using (var selectCmd = connection.CreateCommand())
            {
                selectCmd.CommandText = "SELECT \"Id\", \"SerialNumber\" FROM \"Machines\" WHERE \"Id\" IS NULL OR trim(\"Id\") = '';";
                using (var reader = selectCmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var id = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                        var sn = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                        machinesToFix.Add((id, sn));
                    }
                }
            }

            if (machinesToFix.Count > 0)
            {
                Console.WriteLine($"[DB CLEANUP] Found {machinesToFix.Count} machines with empty or null ID. Fixing...");
                
                foreach (var machine in machinesToFix)
                {
                    string newId;
                    bool exists;
                    do
                    {
                        newId = "VM-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
                        using (var checkCmd = connection.CreateCommand())
                        {
                            checkCmd.CommandText = "SELECT COUNT(1) FROM \"Machines\" WHERE \"Id\" = @Id;";
                            var p = checkCmd.CreateParameter();
                            p.ParameterName = "@Id";
                            p.Value = newId;
                            checkCmd.Parameters.Add(p);
                            exists = Convert.ToInt32(checkCmd.ExecuteScalar()) > 0;
                        }
                    } while (exists);

                    using (var transaction = connection.BeginTransaction())
                    {
                        try
                        {
                            using (var updateFkCmd = connection.CreateCommand())
                            {
                                updateFkCmd.Transaction = transaction;
                                updateFkCmd.CommandText = "UPDATE \"PaymentTransactions\" SET \"MachineId\" = @NewId WHERE \"MachineId\" = @OldId;";
                                
                                var pNew = updateFkCmd.CreateParameter();
                                pNew.ParameterName = "@NewId";
                                pNew.Value = newId;
                                updateFkCmd.Parameters.Add(pNew);

                                var pOld = updateFkCmd.CreateParameter();
                                pOld.ParameterName = "@OldId";
                                pOld.Value = machine.OldId;
                                updateFkCmd.Parameters.Add(pOld);

                                updateFkCmd.ExecuteNonQuery();
                            }

                            using (var updateIdCmd = connection.CreateCommand())
                            {
                                updateIdCmd.Transaction = transaction;
                                updateIdCmd.CommandText = "UPDATE \"Machines\" SET \"Id\" = @NewId WHERE \"Id\" = @OldId AND \"SerialNumber\" = @SN;";
                                
                                var pNew = updateIdCmd.CreateParameter();
                                pNew.ParameterName = "@NewId";
                                pNew.Value = newId;
                                updateIdCmd.Parameters.Add(pNew);

                                var pOld = updateIdCmd.CreateParameter();
                                pOld.ParameterName = "@OldId";
                                pOld.Value = machine.OldId;
                                updateIdCmd.Parameters.Add(pOld);

                                var pSn = updateIdCmd.CreateParameter();
                                pSn.ParameterName = "@SN";
                                pSn.Value = machine.SerialNumber;
                                updateIdCmd.Parameters.Add(pSn);

                                updateIdCmd.ExecuteNonQuery();
                            }

                            transaction.Commit();
                            Console.WriteLine($"[DB CLEANUP] Successfully updated machine (SN: {machine.SerialNumber}) from ID '{machine.OldId}' to '{newId}'.");
                        }
                        catch (Exception ex)
                        {
                            transaction.Rollback();
                            Console.WriteLine($"[DB CLEANUP] Error updating machine ID: {ex.Message}");
                            throw;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB CLEANUP] Exception in CleanUpEmptyMachineIds: {ex.Message}");
        }
        finally
        {
            if (shouldClose)
            {
                connection.Close();
            }
        }
    }
}
