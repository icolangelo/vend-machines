SET lock_timeout = '5s';
CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260606150823_InitialCreate') THEN
    CREATE TABLE "Machines" (
        "Id" text NOT NULL,
        "Name" text NOT NULL,
        "ClientName" text NOT NULL,
        "Location" text NOT NULL,
        "Status" text NOT NULL,
        "StockLevel" integer NOT NULL,
        "Revenue30d" numeric(18,2) NOT NULL,
        "TotalSales30d" integer NOT NULL,
        "LastSync" text NOT NULL,
        "SerialNumber" text NOT NULL,
        CONSTRAINT "PK_Machines" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260606150823_InitialCreate') THEN
    CREATE TABLE "ProductPerformances" (
        "Name" text NOT NULL,
        "TotalSold" integer NOT NULL,
        "Revenue" numeric(18,2) NOT NULL,
        "Trend" double precision NOT NULL,
        "IsTop" boolean NOT NULL,
        CONSTRAINT "PK_ProductPerformances" PRIMARY KEY ("Name")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260606150823_InitialCreate') THEN
    CREATE TABLE "Products" (
        "Id" text NOT NULL,
        "Code" text NOT NULL,
        "Name" text NOT NULL,
        "Description" text NOT NULL,
        "TypeId" text NOT NULL,
        "IsAlcoholic" boolean NOT NULL,
        "Cost" numeric(18,3) NOT NULL,
        CONSTRAINT "PK_Products" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260606150823_InitialCreate') THEN
    CREATE TABLE "ProductTypes" (
        "Id" text NOT NULL,
        "Name" text NOT NULL,
        CONSTRAINT "PK_ProductTypes" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260606150823_InitialCreate') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260606150823_InitialCreate', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260609132837_AddUsersAndCompanies') THEN
    ALTER TABLE "Machines" ADD "CompanyId" uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260609132837_AddUsersAndCompanies') THEN
    CREATE TABLE "Companies" (
        "Id" uuid NOT NULL,
        "Name" text NOT NULL,
        "CreatedByUserId" uuid NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_Companies" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260609132837_AddUsersAndCompanies') THEN
    CREATE TABLE "Users" (
        "Id" uuid NOT NULL,
        "Name" text NOT NULL,
        "Email" text NOT NULL,
        "PasswordHash" text NOT NULL,
        "Role" text NOT NULL,
        "CompanyId" uuid,
        "CreatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_Users" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_Users_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies" ("Id") ON DELETE SET NULL
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260609132837_AddUsersAndCompanies') THEN
    CREATE INDEX "IX_Machines_CompanyId" ON "Machines" ("CompanyId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260609132837_AddUsersAndCompanies') THEN
    CREATE INDEX "IX_Users_CompanyId" ON "Users" ("CompanyId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260609132837_AddUsersAndCompanies') THEN
    CREATE UNIQUE INDEX "IX_Users_Email" ON "Users" ("Email");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260609132837_AddUsersAndCompanies') THEN
    ALTER TABLE "Machines" ADD CONSTRAINT "FK_Machines_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies" ("Id") ON DELETE SET NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260609132837_AddUsersAndCompanies') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260609132837_AddUsersAndCompanies', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260611003133_AddCpfAndCnpj') THEN
    ALTER TABLE "Users" ADD "Cpf" text NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260611003133_AddCpfAndCnpj') THEN
    ALTER TABLE "Companies" ADD "Cnpj" text NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260611003133_AddCpfAndCnpj') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260611003133_AddCpfAndCnpj', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260611004508_AddPrivacyPolicyConsent') THEN
    ALTER TABLE "Users" ADD "AcceptedPrivacyPolicy" boolean NOT NULL DEFAULT FALSE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260611004508_AddPrivacyPolicyConsent') THEN
    ALTER TABLE "Users" ADD "PrivacyPolicyAcceptedAt" timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260611004508_AddPrivacyPolicyConsent') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260611004508_AddPrivacyPolicyConsent', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260613184854_AddMercadoPagoIntegration') THEN
    ALTER TABLE "Machines" ADD "MercadoPagoEnabled" boolean NOT NULL DEFAULT FALSE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260613184854_AddMercadoPagoIntegration') THEN
    CREATE TABLE "SystemSettings" (
        "Id" uuid NOT NULL,
        "ApplicationFeePercent" numeric(5,2) NOT NULL,
        "UpdatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_SystemSettings" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260613184854_AddMercadoPagoIntegration') THEN
    CREATE TABLE "MercadoPagoIntegrations" (
        "Id" uuid NOT NULL,
        "CompanyId" uuid NOT NULL,
        "OwnerName" text NOT NULL,
        "OwnerCpf" text NOT NULL,
        "OwnerEmail" text NOT NULL,
        "OwnerPhone" text NOT NULL,
        "BusinessName" text NOT NULL,
        "TradeName" text NOT NULL,
        "Cnpj" text NOT NULL,
        "BusinessEmail" text NOT NULL,
        "BusinessPhone" text NOT NULL,
        "AccessToken" text NOT NULL,
        "PublicKey" text NOT NULL,
        "ClientId" text NOT NULL,
        "ClientSecret" text NOT NULL,
        "IsActive" boolean NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        CONSTRAINT "PK_MercadoPagoIntegrations" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_MercadoPagoIntegrations_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260613184854_AddMercadoPagoIntegration') THEN
    CREATE TABLE "PaymentTransactions" (
        "Id" uuid NOT NULL,
        "MachineId" text NOT NULL,
        "CompanyId" uuid NOT NULL,
        "Amount" numeric(18,2) NOT NULL,
        "ApplicationFee" numeric(18,2) NOT NULL,
        "Status" text NOT NULL,
        "MercadoPagoPaymentId" text,
        "MercadoPagoStatus" text,
        "MercadoPagoStatusDetail" text,
        "RawResponse" text,
        "QrCode" text NOT NULL,
        "QrCodeBase64" text NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "CompletedAt" timestamp with time zone,
        CONSTRAINT "PK_PaymentTransactions" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_PaymentTransactions_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies" ("Id") ON DELETE CASCADE,
        CONSTRAINT "FK_PaymentTransactions_Machines_MachineId" FOREIGN KEY ("MachineId") REFERENCES "Machines" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260613184854_AddMercadoPagoIntegration') THEN
    CREATE TABLE "TransactionTelemetryLogs" (
        "Id" uuid NOT NULL,
        "TransactionId" uuid NOT NULL,
        "Timestamp" timestamp with time zone NOT NULL,
        "LogType" text NOT NULL,
        "Message" text NOT NULL,
        CONSTRAINT "PK_TransactionTelemetryLogs" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_TransactionTelemetryLogs_PaymentTransactions_TransactionId" FOREIGN KEY ("TransactionId") REFERENCES "PaymentTransactions" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260613184854_AddMercadoPagoIntegration') THEN
    CREATE INDEX "IX_MercadoPagoIntegrations_CompanyId" ON "MercadoPagoIntegrations" ("CompanyId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260613184854_AddMercadoPagoIntegration') THEN
    CREATE INDEX "IX_PaymentTransactions_CompanyId" ON "PaymentTransactions" ("CompanyId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260613184854_AddMercadoPagoIntegration') THEN
    CREATE INDEX "IX_PaymentTransactions_MachineId" ON "PaymentTransactions" ("MachineId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260613184854_AddMercadoPagoIntegration') THEN
    CREATE INDEX "IX_TransactionTelemetryLogs_TransactionId" ON "TransactionTelemetryLogs" ("TransactionId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260613184854_AddMercadoPagoIntegration') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260613184854_AddMercadoPagoIntegration', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260627163422_RemoveStaticIntegrations') THEN
    DELETE FROM "MercadoPagoIntegrations" WHERE "ClientId" IS NULL OR "ClientId" = '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260627163422_RemoveStaticIntegrations') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260627163422_RemoveStaticIntegrations', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260628002415_AddMercadoPagoOAuthDiagnostics') THEN
    ALTER TABLE "MercadoPagoIntegrations" ADD "AccessTokenExpiresAt" timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260628002415_AddMercadoPagoOAuthDiagnostics') THEN
    ALTER TABLE "MercadoPagoIntegrations" ADD "LastTokenValidationAt" timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260628002415_AddMercadoPagoOAuthDiagnostics') THEN
    ALTER TABLE "MercadoPagoIntegrations" ADD "LastTokenValidationStatus" text NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260628002415_AddMercadoPagoOAuthDiagnostics') THEN
    ALTER TABLE "MercadoPagoIntegrations" ADD "MercadoPagoNickname" text NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260628002415_AddMercadoPagoOAuthDiagnostics') THEN
    ALTER TABLE "MercadoPagoIntegrations" ADD "MercadoPagoSiteId" text NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260628002415_AddMercadoPagoOAuthDiagnostics') THEN
    ALTER TABLE "MercadoPagoIntegrations" ADD "MercadoPagoUserId" text NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260628002415_AddMercadoPagoOAuthDiagnostics') THEN
    ALTER TABLE "MercadoPagoIntegrations" ADD "RefreshToken" text NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260628002415_AddMercadoPagoOAuthDiagnostics') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260628002415_AddMercadoPagoOAuthDiagnostics', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260628125424_AddLocationsToMachines') THEN
    ALTER TABLE "Machines" ADD "LocationId" uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260628125424_AddLocationsToMachines') THEN
    CREATE TABLE "Locations" (
        "Id" uuid NOT NULL,
        "CompanyId" uuid NOT NULL,
        "Name" text NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone,
        CONSTRAINT "PK_Locations" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_Locations_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260628125424_AddLocationsToMachines') THEN
    INSERT INTO "Locations" ("Id", "CompanyId", "Name", "CreatedAt")
    SELECT md5(random()::text || clock_timestamp()::text)::uuid, m."CompanyId", trim(m."ClientName"), now()
    FROM "Machines" m
    WHERE m."CompanyId" IS NOT NULL
      AND trim(m."ClientName") <> ''
    GROUP BY m."CompanyId", trim(m."ClientName");

    INSERT INTO "Locations" ("Id", "CompanyId", "Name", "CreatedAt")
    SELECT md5(random()::text || clock_timestamp()::text)::uuid, c."Id", COALESCE(NULLIF(split_part(trim(c."Name"), ' ', 1), ''), 'Empresa') || ' - Shopping', now()
    FROM "Companies" c
    WHERE NOT EXISTS (
        SELECT 1 FROM "Locations" l
        WHERE l."CompanyId" = c."Id"
          AND l."Name" = COALESCE(NULLIF(split_part(trim(c."Name"), ' ', 1), ''), 'Empresa') || ' - Shopping'
    );

    INSERT INTO "Locations" ("Id", "CompanyId", "Name", "CreatedAt")
    SELECT md5(random()::text || clock_timestamp()::text)::uuid, c."Id", COALESCE(NULLIF(split_part(trim(c."Name"), ' ', 1), ''), 'Empresa') || ' - Hospital', now()
    FROM "Companies" c
    WHERE NOT EXISTS (
        SELECT 1 FROM "Locations" l
        WHERE l."CompanyId" = c."Id"
          AND l."Name" = COALESCE(NULLIF(split_part(trim(c."Name"), ' ', 1), ''), 'Empresa') || ' - Hospital'
    );

    UPDATE "Machines" m
    SET "LocationId" = l."Id",
        "ClientName" = l."Name"
    FROM "Locations" l
    WHERE m."CompanyId" = l."CompanyId"
      AND trim(m."ClientName") = l."Name"
      AND m."LocationId" IS NULL;

    WITH first_location AS (
        SELECT DISTINCT ON ("CompanyId") "CompanyId", "Id", "Name"
        FROM "Locations"
        ORDER BY "CompanyId", "CreatedAt", "Name"
    )
    UPDATE "Machines" m
    SET "LocationId" = fl."Id",
        "ClientName" = fl."Name"
    FROM first_location fl
    WHERE m."CompanyId" = fl."CompanyId"
      AND m."LocationId" IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260628125424_AddLocationsToMachines') THEN
    CREATE INDEX "IX_Machines_LocationId" ON "Machines" ("LocationId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260628125424_AddLocationsToMachines') THEN
    CREATE UNIQUE INDEX "IX_Locations_CompanyId_Name" ON "Locations" ("CompanyId", "Name");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260628125424_AddLocationsToMachines') THEN
    ALTER TABLE "Machines" ADD CONSTRAINT "FK_Machines_Locations_LocationId" FOREIGN KEY ("LocationId") REFERENCES "Locations" ("Id") ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260628125424_AddLocationsToMachines') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260628125424_AddLocationsToMachines', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260628134337_AddCompanyIdToProducts') THEN
    ALTER TABLE "ProductTypes" ADD "CompanyId" uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260628134337_AddCompanyIdToProducts') THEN
    ALTER TABLE "Products" ADD "CompanyId" uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260628134337_AddCompanyIdToProducts') THEN
    CREATE INDEX "IX_ProductTypes_CompanyId" ON "ProductTypes" ("CompanyId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260628134337_AddCompanyIdToProducts') THEN
    CREATE INDEX "IX_Products_CompanyId" ON "Products" ("CompanyId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260628134337_AddCompanyIdToProducts') THEN
    ALTER TABLE "Products" ADD CONSTRAINT "FK_Products_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies" ("Id") ON DELETE CASCADE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260628134337_AddCompanyIdToProducts') THEN
    ALTER TABLE "ProductTypes" ADD CONSTRAINT "FK_ProductTypes_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies" ("Id") ON DELETE CASCADE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260628134337_AddCompanyIdToProducts') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260628134337_AddCompanyIdToProducts', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260628151756_AddOriginalIdToProducts') THEN
    ALTER TABLE "ProductTypes" ADD "OriginalId" TEXT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260628151756_AddOriginalIdToProducts') THEN
    ALTER TABLE "Products" ADD "OriginalId" TEXT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260628151756_AddOriginalIdToProducts') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260628151756_AddOriginalIdToProducts', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260628235522_AddSendTelemetryToPaymentTransactions') THEN
    DO $$
    BEGIN
        IF NOT EXISTS (
            SELECT 1
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'PaymentTransactions'
              AND column_name = 'SendTelemetryToMachine'
        ) THEN
            ALTER TABLE "PaymentTransactions"
            ADD COLUMN "SendTelemetryToMachine" integer NOT NULL DEFAULT 1;
        END IF;
    END $$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260628235522_AddSendTelemetryToPaymentTransactions') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260628235522_AddSendTelemetryToPaymentTransactions', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Users" ALTER COLUMN "Role" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Users" ALTER COLUMN "PrivacyPolicyAcceptedAt" TYPE timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Users" ALTER COLUMN "PasswordHash" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Users" ALTER COLUMN "Name" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Users" ALTER COLUMN "Email" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Users" ALTER COLUMN "CreatedAt" TYPE timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Users" ALTER COLUMN "Cpf" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Users" ALTER COLUMN "CompanyId" TYPE uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Users" ALTER COLUMN "AcceptedPrivacyPolicy" TYPE boolean;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Users" ALTER COLUMN "Id" TYPE uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "TransactionTelemetryLogs" ALTER COLUMN "TransactionId" TYPE uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "TransactionTelemetryLogs" ALTER COLUMN "Timestamp" TYPE timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "TransactionTelemetryLogs" ALTER COLUMN "Message" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "TransactionTelemetryLogs" ALTER COLUMN "LogType" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "TransactionTelemetryLogs" ALTER COLUMN "Id" TYPE uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "SystemSettings" ALTER COLUMN "UpdatedAt" TYPE timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "SystemSettings" ALTER COLUMN "ApplicationFeePercent" TYPE numeric(5,2);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "SystemSettings" ALTER COLUMN "Id" TYPE uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "ProductTypes" ALTER COLUMN "OriginalId" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "ProductTypes" ALTER COLUMN "Name" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "ProductTypes" ALTER COLUMN "CompanyId" TYPE uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "ProductTypes" ALTER COLUMN "Id" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Products" ALTER COLUMN "TypeId" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Products" ALTER COLUMN "OriginalId" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Products" ALTER COLUMN "Name" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Products" ALTER COLUMN "IsAlcoholic" TYPE boolean;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Products" ALTER COLUMN "Description" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Products" ALTER COLUMN "Cost" TYPE numeric(18,3);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Products" ALTER COLUMN "CompanyId" TYPE uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Products" ALTER COLUMN "Code" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Products" ALTER COLUMN "Id" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "ProductPerformances" ALTER COLUMN "Trend" TYPE double precision;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "ProductPerformances" ALTER COLUMN "TotalSold" TYPE integer;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "ProductPerformances" ALTER COLUMN "Revenue" TYPE numeric(18,2);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "ProductPerformances" ALTER COLUMN "IsTop" TYPE boolean;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "ProductPerformances" ALTER COLUMN "Name" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "PaymentTransactions" ALTER COLUMN "Status" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "PaymentTransactions" ALTER COLUMN "SendTelemetryToMachine" TYPE integer;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "PaymentTransactions" ALTER COLUMN "RawResponse" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "PaymentTransactions" ALTER COLUMN "QrCodeBase64" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "PaymentTransactions" ALTER COLUMN "QrCode" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "PaymentTransactions" ALTER COLUMN "MercadoPagoStatusDetail" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "PaymentTransactions" ALTER COLUMN "MercadoPagoStatus" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "PaymentTransactions" ALTER COLUMN "MercadoPagoPaymentId" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "PaymentTransactions" ALTER COLUMN "MachineId" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "PaymentTransactions" ALTER COLUMN "CreatedAt" TYPE timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "PaymentTransactions" ALTER COLUMN "CompletedAt" TYPE timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "PaymentTransactions" ALTER COLUMN "CompanyId" TYPE uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "PaymentTransactions" ALTER COLUMN "ApplicationFee" TYPE numeric(18,2);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "PaymentTransactions" ALTER COLUMN "Amount" TYPE numeric(18,2);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "PaymentTransactions" ALTER COLUMN "Id" TYPE uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "UpdatedAt" TYPE timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "TradeName" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "RefreshToken" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "PublicKey" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "OwnerPhone" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "OwnerName" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "OwnerEmail" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "OwnerCpf" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "MercadoPagoUserId" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "MercadoPagoSiteId" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "MercadoPagoNickname" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "LastTokenValidationStatus" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "LastTokenValidationAt" TYPE timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "IsActive" TYPE boolean;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "CreatedAt" TYPE timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "CompanyId" TYPE uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "Cnpj" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "ClientSecret" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "ClientId" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "BusinessPhone" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "BusinessName" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "BusinessEmail" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "AccessTokenExpiresAt" TYPE timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "AccessToken" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "Id" TYPE uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Machines" ALTER COLUMN "TotalSales30d" TYPE integer;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Machines" ALTER COLUMN "StockLevel" TYPE integer;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Machines" ALTER COLUMN "Status" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Machines" ALTER COLUMN "SerialNumber" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Machines" ALTER COLUMN "Revenue30d" TYPE numeric(18,2);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Machines" ALTER COLUMN "Name" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Machines" ALTER COLUMN "MercadoPagoEnabled" TYPE boolean;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Machines" ALTER COLUMN "LocationId" TYPE uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Machines" ALTER COLUMN "Location" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Machines" ALTER COLUMN "LastSync" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Machines" ALTER COLUMN "CompanyId" TYPE uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Machines" ALTER COLUMN "ClientName" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Machines" ALTER COLUMN "Id" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Machines" ADD "NormalizedSerialNumber" text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Locations" ALTER COLUMN "UpdatedAt" TYPE timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Locations" ALTER COLUMN "Name" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Locations" ALTER COLUMN "CreatedAt" TYPE timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Locations" ALTER COLUMN "CompanyId" TYPE uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Locations" ALTER COLUMN "Id" TYPE uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Companies" ALTER COLUMN "Name" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Companies" ALTER COLUMN "CreatedByUserId" TYPE uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Companies" ALTER COLUMN "CreatedAt" TYPE timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Companies" ALTER COLUMN "Cnpj" TYPE text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    ALTER TABLE "Companies" ALTER COLUMN "Id" TYPE uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    CREATE TABLE "DeliveryFailures" (
        "Id" uuid NOT NULL,
        "CompanyId" uuid NOT NULL,
        "MachineId" text NOT NULL,
        "SessionId" uuid NOT NULL,
        "TransactionId" uuid NOT NULL,
        "Reason" text NOT NULL,
        "OriginalReason" text NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_DeliveryFailures" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    CREATE TABLE "MachineConnectionStates" (
        "Id" uuid NOT NULL,
        "MachineId" text NOT NULL,
        "CompanyId" uuid NOT NULL,
        "MonitoringEnabled" boolean NOT NULL,
        "IsOnline" boolean NOT NULL,
        "ConnectionId" text,
        "ConnectedAt" timestamp with time zone,
        "DisconnectedAt" timestamp with time zone,
        "LastSeenAt" timestamp with time zone,
        "ActivatedAt" timestamp with time zone,
        "ActivatedByUserId" uuid,
        "DeactivatedAt" timestamp with time zone,
        "DeactivatedByUserId" uuid,
        CONSTRAINT "PK_MachineConnectionStates" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_MachineConnectionStates_Machines_MachineId" FOREIGN KEY ("MachineId") REFERENCES "Machines" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    CREATE TABLE "MachineSessions" (
        "Id" uuid NOT NULL,
        "MachineId" text NOT NULL,
        "CompanyId" uuid NOT NULL,
        "StartedByUserId" uuid,
        "Source" text NOT NULL,
        "State" text NOT NULL,
        "CloseReason" text,
        "ItemNumber" integer,
        "AmountCents" integer,
        "TransactionId" uuid,
        "StartedAt" timestamp with time zone NOT NULL,
        "LastEventAt" timestamp with time zone NOT NULL,
        "SelectionDeadlineAt" timestamp with time zone,
        "PaymentDeadlineAt" timestamp with time zone,
        "DeliveryDeadlineAt" timestamp with time zone,
        "ClosedAt" timestamp with time zone,
        CONSTRAINT "PK_MachineSessions" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_MachineSessions_Machines_MachineId" FOREIGN KEY ("MachineId") REFERENCES "Machines" ("Id") ON DELETE CASCADE,
        CONSTRAINT "FK_MachineSessions_PaymentTransactions_TransactionId" FOREIGN KEY ("TransactionId") REFERENCES "PaymentTransactions" ("Id") ON DELETE SET NULL
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    CREATE TABLE "OutboxMessages" (
        "Id" uuid NOT NULL,
        "CompanyId" uuid NOT NULL,
        "MachineId" text NOT NULL,
        "SessionId" uuid,
        "TransactionId" uuid,
        "MessageType" text NOT NULL,
        "PayloadJson" text NOT NULL,
        "Status" text NOT NULL,
        "Attempts" integer NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "NextAttemptAt" timestamp with time zone,
        "ProcessedAt" timestamp with time zone,
        "LastError" text,
        CONSTRAINT "PK_OutboxMessages" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    CREATE TABLE "TelemetryCommands" (
        "Id" uuid NOT NULL,
        "CompanyId" uuid NOT NULL,
        "MachineId" text NOT NULL,
        "SessionId" uuid,
        "TransactionId" uuid,
        "UserId" uuid,
        "MessageId" integer NOT NULL,
        "Direction" text NOT NULL,
        "Command" text NOT NULL,
        "DataJson" text,
        "Status" text NOT NULL,
        "Attempts" integer NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "SentAt" timestamp with time zone,
        "AckAt" timestamp with time zone,
        "Error" text,
        CONSTRAINT "PK_TelemetryCommands" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    CREATE TABLE "TelemetryEvents" (
        "Id" uuid NOT NULL,
        "CompanyId" uuid NOT NULL,
        "MachineId" text NOT NULL,
        "SessionId" uuid,
        "TransactionId" uuid,
        "UserId" uuid,
        "EventType" text NOT NULL,
        "Detail" text,
        "DataJson" text,
        "CreatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_TelemetryEvents" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    CREATE UNIQUE INDEX "IX_Machines_NormalizedSerialNumber" ON "Machines" ("NormalizedSerialNumber");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    CREATE UNIQUE INDEX "IX_DeliveryFailures_TransactionId" ON "DeliveryFailures" ("TransactionId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    CREATE INDEX "IX_MachineConnectionStates_CompanyId_IsOnline" ON "MachineConnectionStates" ("CompanyId", "IsOnline");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    CREATE UNIQUE INDEX "IX_MachineConnectionStates_MachineId" ON "MachineConnectionStates" ("MachineId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    CREATE INDEX "IX_MachineSessions_CompanyId_MachineId_State" ON "MachineSessions" ("CompanyId", "MachineId", "State");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    CREATE INDEX "IX_MachineSessions_MachineId" ON "MachineSessions" ("MachineId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    CREATE UNIQUE INDEX "IX_MachineSessions_TransactionId" ON "MachineSessions" ("TransactionId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    CREATE INDEX "IX_OutboxMessages_Status_NextAttemptAt" ON "OutboxMessages" ("Status", "NextAttemptAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    CREATE INDEX "IX_TelemetryCommands_CompanyId_MachineId_CreatedAt" ON "TelemetryCommands" ("CompanyId", "MachineId", "CreatedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    CREATE INDEX "IX_TelemetryEvents_CompanyId_MachineId_CreatedAt" ON "TelemetryEvents" ("CompanyId", "MachineId", "CreatedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260715023221_AddTelemetrySessionManagement') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260715023221_AddTelemetrySessionManagement', '10.0.12');
    END IF;
END $EF$;
COMMIT;

