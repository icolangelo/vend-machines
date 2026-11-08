SET lock_timeout = '5s';
START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926175158_AddSalesInventoryLedger') THEN
    CREATE TABLE "MachineProductSlots" (
        "Id" uuid NOT NULL,
        "CompanyId" uuid NOT NULL,
        "MachineId" text NOT NULL,
        "ItemNumber" integer NOT NULL,
        "ProductId" text NOT NULL,
        "Capacity" integer NOT NULL,
        "CurrentStock" integer NOT NULL,
        "LowStockThreshold" integer NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_MachineProductSlots" PRIMARY KEY ("Id"),
        CONSTRAINT "AK_MachineProductSlots_MachineId_ItemNumber" UNIQUE ("MachineId", "ItemNumber"),
        CONSTRAINT "FK_MachineProductSlots_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies" ("Id") ON DELETE CASCADE,
        CONSTRAINT "FK_MachineProductSlots_Machines_MachineId" FOREIGN KEY ("MachineId") REFERENCES "Machines" ("Id") ON DELETE CASCADE,
        CONSTRAINT "FK_MachineProductSlots_Products_ProductId" FOREIGN KEY ("ProductId") REFERENCES "Products" ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926175158_AddSalesInventoryLedger') THEN
    CREATE TABLE "SaleRecords" (
        "Id" uuid NOT NULL,
        "CompanyId" uuid NOT NULL,
        "MachineId" text NOT NULL,
        "SessionId" uuid NOT NULL,
        "TransactionId" uuid NOT NULL,
        "ItemNumber" integer NOT NULL,
        "ProductId" text,
        "ProductNameSnapshot" character varying(256),
        "Quantity" integer NOT NULL,
        "GrossAmount" numeric NOT NULL,
        "ApplicationFee" numeric NOT NULL,
        "NetAmount" numeric NOT NULL,
        "SoldAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_SaleRecords" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_SaleRecords_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies" ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_SaleRecords_MachineSessions_SessionId" FOREIGN KEY ("SessionId") REFERENCES "MachineSessions" ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_SaleRecords_Machines_MachineId" FOREIGN KEY ("MachineId") REFERENCES "Machines" ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_SaleRecords_PaymentTransactions_TransactionId" FOREIGN KEY ("TransactionId") REFERENCES "PaymentTransactions" ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_SaleRecords_Products_ProductId" FOREIGN KEY ("ProductId") REFERENCES "Products" ("Id") ON DELETE SET NULL
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926175158_AddSalesInventoryLedger') THEN
    CREATE TABLE "InventoryMovements" (
        "Id" uuid NOT NULL,
        "CompanyId" uuid NOT NULL,
        "MachineId" text NOT NULL,
        "ItemNumber" integer NOT NULL,
        "ProductId" text NOT NULL,
        "SaleRecordId" uuid,
        "UserId" uuid,
        "MovementType" character varying(32) NOT NULL,
        "QuantityDelta" integer NOT NULL,
        "QuantityAfter" integer NOT NULL,
        "Reason" character varying(500) NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_InventoryMovements" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_InventoryMovements_MachineProductSlots_MachineId_ItemNumber" FOREIGN KEY ("MachineId", "ItemNumber") REFERENCES "MachineProductSlots" ("MachineId", "ItemNumber") ON DELETE RESTRICT,
        CONSTRAINT "FK_InventoryMovements_SaleRecords_SaleRecordId" FOREIGN KEY ("SaleRecordId") REFERENCES "SaleRecords" ("Id") ON DELETE SET NULL
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926175158_AddSalesInventoryLedger') THEN
    CREATE INDEX "IX_InventoryMovements_MachineId_ItemNumber_CreatedAt" ON "InventoryMovements" ("MachineId", "ItemNumber", "CreatedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926175158_AddSalesInventoryLedger') THEN
    CREATE INDEX "IX_InventoryMovements_SaleRecordId" ON "InventoryMovements" ("SaleRecordId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926175158_AddSalesInventoryLedger') THEN
    CREATE INDEX "IX_MachineProductSlots_CompanyId_ProductId" ON "MachineProductSlots" ("CompanyId", "ProductId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926175158_AddSalesInventoryLedger') THEN
    CREATE UNIQUE INDEX "IX_MachineProductSlots_MachineId_ItemNumber" ON "MachineProductSlots" ("MachineId", "ItemNumber");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926175158_AddSalesInventoryLedger') THEN
    CREATE INDEX "IX_MachineProductSlots_ProductId" ON "MachineProductSlots" ("ProductId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926175158_AddSalesInventoryLedger') THEN
    CREATE INDEX "IX_SaleRecords_CompanyId_ProductId_SoldAt" ON "SaleRecords" ("CompanyId", "ProductId", "SoldAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926175158_AddSalesInventoryLedger') THEN
    CREATE INDEX "IX_SaleRecords_CompanyId_SoldAt" ON "SaleRecords" ("CompanyId", "SoldAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926175158_AddSalesInventoryLedger') THEN
    CREATE INDEX "IX_SaleRecords_MachineId" ON "SaleRecords" ("MachineId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926175158_AddSalesInventoryLedger') THEN
    CREATE INDEX "IX_SaleRecords_ProductId" ON "SaleRecords" ("ProductId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926175158_AddSalesInventoryLedger') THEN
    CREATE UNIQUE INDEX "IX_SaleRecords_SessionId" ON "SaleRecords" ("SessionId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926175158_AddSalesInventoryLedger') THEN
    CREATE UNIQUE INDEX "IX_SaleRecords_TransactionId" ON "SaleRecords" ("TransactionId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926175158_AddSalesInventoryLedger') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260926175158_AddSalesInventoryLedger', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926180228_RestrictSalesProductDelete') THEN
    ALTER TABLE "SaleRecords" DROP CONSTRAINT "FK_SaleRecords_Products_ProductId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926180228_RestrictSalesProductDelete') THEN
    ALTER TABLE "SaleRecords" ADD CONSTRAINT "FK_SaleRecords_Products_ProductId" FOREIGN KEY ("ProductId") REFERENCES "Products" ("Id") ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926180228_RestrictSalesProductDelete') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260926180228_RestrictSalesProductDelete', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926180400_BackfillDeliveredSales') THEN
    ALTER TABLE "SaleRecords" ADD "Origin" character varying(32) NOT NULL DEFAULT 'DeliveryConfirmed';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926180400_BackfillDeliveredSales') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260926180400_BackfillDeliveredSales', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926180751_TrackSaleRefunds') THEN
    ALTER TABLE "SaleRecords" ADD "FinancialStatus" character varying(32) NOT NULL DEFAULT 'Settled';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926180751_TrackSaleRefunds') THEN
    ALTER TABLE "SaleRecords" ADD "RefundedAmount" numeric NOT NULL DEFAULT 0.0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926180751_TrackSaleRefunds') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260926180751_TrackSaleRefunds', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926181536_AddMachineActivationStatus') THEN
    ALTER TABLE "Machines" ADD "IsActive" boolean NOT NULL DEFAULT TRUE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926181536_AddMachineActivationStatus') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260926181536_AddMachineActivationStatus', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926211028_AddOperationalAlerts') THEN
    CREATE TABLE "OperationalAlerts" (
        "Id" uuid NOT NULL,
        "CompanyId" uuid NOT NULL,
        "MachineId" text,
        "SessionId" uuid,
        "TransactionId" uuid,
        "DeduplicationKey" character varying(256) NOT NULL,
        "Type" character varying(48) NOT NULL,
        "Severity" character varying(16) NOT NULL,
        "Status" character varying(16) NOT NULL,
        "Title" character varying(200) NOT NULL,
        "Description" character varying(1000) NOT NULL,
        "OccurredAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone NOT NULL,
        "ResolvedAt" timestamp with time zone,
        "Resolution" character varying(500),
        CONSTRAINT "PK_OperationalAlerts" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_OperationalAlerts_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926211028_AddOperationalAlerts') THEN
    CREATE TABLE "AlertReadReceipts" (
        "AlertId" uuid NOT NULL,
        "UserId" uuid NOT NULL,
        "ReadAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_AlertReadReceipts" PRIMARY KEY ("AlertId", "UserId"),
        CONSTRAINT "FK_AlertReadReceipts_OperationalAlerts_AlertId" FOREIGN KEY ("AlertId") REFERENCES "OperationalAlerts" ("Id") ON DELETE CASCADE,
        CONSTRAINT "FK_AlertReadReceipts_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926211028_AddOperationalAlerts') THEN
    CREATE INDEX "IX_AlertReadReceipts_UserId" ON "AlertReadReceipts" ("UserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926211028_AddOperationalAlerts') THEN
    CREATE UNIQUE INDEX "IX_OperationalAlerts_CompanyId_DeduplicationKey" ON "OperationalAlerts" ("CompanyId", "DeduplicationKey");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926211028_AddOperationalAlerts') THEN
    CREATE INDEX "IX_OperationalAlerts_CompanyId_Status_UpdatedAt" ON "OperationalAlerts" ("CompanyId", "Status", "UpdatedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926211028_AddOperationalAlerts') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260926211028_AddOperationalAlerts', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927135543_AddDeviceMessageInbox') THEN
    CREATE TABLE "DeviceMessageInbox" (
        "Id" uuid NOT NULL,
        "IdempotencyKey" character varying(64) NOT NULL,
        "MachineId" text NOT NULL,
        "CompanyId" uuid NOT NULL,
        "Serial" character varying(64) NOT NULL,
        "ConnectionId" character varying(64) NOT NULL,
        "MessageId" integer NOT NULL,
        "DataJson" text NOT NULL,
        "ReceivedAt" timestamp with time zone NOT NULL,
        "Status" character varying(16) NOT NULL,
        "Attempts" integer NOT NULL,
        "NextAttemptAt" timestamp with time zone,
        "ProcessingStartedAt" timestamp with time zone,
        "ProcessedAt" timestamp with time zone,
        "LastError" character varying(2000),
        CONSTRAINT "PK_DeviceMessageInbox" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927135543_AddDeviceMessageInbox') THEN
    CREATE UNIQUE INDEX "IX_DeviceMessageInbox_IdempotencyKey" ON "DeviceMessageInbox" ("IdempotencyKey");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927135543_AddDeviceMessageInbox') THEN
    CREATE INDEX "IX_DeviceMessageInbox_MachineId_Status_ReceivedAt" ON "DeviceMessageInbox" ("MachineId", "Status", "ReceivedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927135543_AddDeviceMessageInbox') THEN
    CREATE INDEX "IX_DeviceMessageInbox_Status_NextAttemptAt_ReceivedAt" ON "DeviceMessageInbox" ("Status", "NextAttemptAt", "ReceivedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927135543_AddDeviceMessageInbox') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260927135543_AddDeviceMessageInbox', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927160644_NormalizeRegistrationIdentifiers') THEN
    ALTER TABLE "Users" ADD "NormalizedCpf" text NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927160644_NormalizeRegistrationIdentifiers') THEN
    ALTER TABLE "Users" ADD "NormalizedEmail" text NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927160644_NormalizeRegistrationIdentifiers') THEN
    ALTER TABLE "Companies" ADD "NormalizedCnpj" text NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927160644_NormalizeRegistrationIdentifiers') THEN
    UPDATE "Users"
    SET "NormalizedEmail" = lower(trim(COALESCE("Email", ''))),
        "NormalizedCpf" = regexp_replace(COALESCE("Cpf", ''), '[^0-9]', '', 'g');
    UPDATE "Companies"
    SET "NormalizedCnpj" = upper(regexp_replace(COALESCE("Cnpj", ''), '[^A-Za-z0-9]', '', 'g'));

    CREATE OR REPLACE FUNCTION normalize_user_registration_identifiers()
    RETURNS trigger
    LANGUAGE plpgsql
    AS $function$
    BEGIN
        NEW."NormalizedEmail" := lower(trim(COALESCE(NEW."Email", '')));
        NEW."NormalizedCpf" := regexp_replace(COALESCE(NEW."Cpf", ''), '[^0-9]', '', 'g');
        RETURN NEW;
    END;
    $function$;

    CREATE TRIGGER "TR_Users_NormalizeRegistrationIdentifiers"
    BEFORE INSERT OR UPDATE ON "Users"
    FOR EACH ROW
    EXECUTE FUNCTION normalize_user_registration_identifiers();

    CREATE OR REPLACE FUNCTION normalize_company_registration_identifier()
    RETURNS trigger
    LANGUAGE plpgsql
    AS $function$
    BEGIN
        NEW."NormalizedCnpj" := upper(regexp_replace(COALESCE(NEW."Cnpj", ''), '[^A-Za-z0-9]', '', 'g'));
        RETURN NEW;
    END;
    $function$;

    CREATE TRIGGER "TR_Companies_NormalizeRegistrationIdentifier"
    BEFORE INSERT OR UPDATE ON "Companies"
    FOR EACH ROW
    EXECUTE FUNCTION normalize_company_registration_identifier();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927160644_NormalizeRegistrationIdentifiers') THEN
    CREATE UNIQUE INDEX "IX_Users_NormalizedCpf" ON "Users" ("NormalizedCpf") WHERE "NormalizedCpf" <> '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927160644_NormalizeRegistrationIdentifiers') THEN
    CREATE UNIQUE INDEX "IX_Users_NormalizedEmail" ON "Users" ("NormalizedEmail") WHERE "NormalizedEmail" <> '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927160644_NormalizeRegistrationIdentifiers') THEN
    CREATE UNIQUE INDEX "IX_Companies_NormalizedCnpj" ON "Companies" ("NormalizedCnpj") WHERE "NormalizedCnpj" <> '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927160644_NormalizeRegistrationIdentifiers') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260927160644_NormalizeRegistrationIdentifiers', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927163720_BootstrapPlatformAdmin') THEN
    DO $bootstrap$
    DECLARE active_accounts integer;
    BEGIN
        SELECT COUNT(*) INTO active_accounts
        FROM "Users"
        WHERE "NormalizedEmail" = 'dev.ivan@gmail.com'
          AND "IsActive" = TRUE;
        IF active_accounts <> 1 THEN
            RAISE EXCEPTION 'PlatformAdmin bootstrap requires exactly one active reserved account; found %', active_accounts;
        END IF;
    END;
    $bootstrap$;

    WITH bootstrap_account AS MATERIALIZED (
        SELECT "Id", "CompanyId", "Role" AS "PreviousRole"
        FROM "Users"
        WHERE "NormalizedEmail" = 'dev.ivan@gmail.com'
          AND "IsActive" = TRUE
    ), promoted AS (
        UPDATE "Users" AS user_account
        SET "Role" = 'PlatformAdmin',
            "CredentialVersion" = user_account."CredentialVersion" + 1
        FROM bootstrap_account
        WHERE user_account."Id" = bootstrap_account."Id"
          AND user_account."Role" <> 'PlatformAdmin'
        RETURNING user_account."Id"
    )
    INSERT INTO "AuditLogs"
        ("Id", "CompanyId", "ActorUserId", "Action", "ResourceType", "ResourceId", "MetadataJson", "CreatedAt")
    SELECT gen_random_uuid(), "CompanyId", NULL,
           'auth.platform_admin_bootstrap_completed', 'User', "Id"::text,
           jsonb_build_object(
               'source', 'migration',
               'roleChanged', ("PreviousRole" <> 'PlatformAdmin')
           )::text,
           CURRENT_TIMESTAMP
    FROM bootstrap_account;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927163720_BootstrapPlatformAdmin') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260927163720_BootstrapPlatformAdmin', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927172108_SetMachineSalesTarget') THEN
    ALTER TABLE "Machines" ADD "MinimumSalesTarget30d" integer;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927172108_SetMachineSalesTarget') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260927172108_SetMachineSalesTarget', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927181221_AddCompanyActivationStatus') THEN
    ALTER TABLE "Companies" ADD "ActivationStatus" character varying(16) NOT NULL DEFAULT 'Active';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927181221_AddCompanyActivationStatus') THEN
    ALTER TABLE "Companies" ALTER COLUMN "ActivationStatus" SET DEFAULT 'Pending';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927181221_AddCompanyActivationStatus') THEN
    CREATE OR REPLACE FUNCTION "DisableUserForPendingCompany"()
    RETURNS trigger AS $$
    BEGIN
        IF NEW."CompanyId" IS NOT NULL AND EXISTS (
            SELECT 1 FROM "Companies"
            WHERE "Id" = NEW."CompanyId" AND "ActivationStatus" <> 'Active'
        ) THEN
            NEW."IsActive" := FALSE;
        END IF;
        RETURN NEW;
    END;
    $$ LANGUAGE plpgsql;

    CREATE TRIGGER "TR_Users_DisableUserForPendingCompany"
    BEFORE INSERT ON "Users"
    FOR EACH ROW EXECUTE FUNCTION "DisableUserForPendingCompany"();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927181221_AddCompanyActivationStatus') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260927181221_AddCompanyActivationStatus', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927202033_RequireEmailVerification') THEN
    ALTER TABLE "Users" ADD "EmailVerificationTokenExpiresAt" timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927202033_RequireEmailVerification') THEN
    ALTER TABLE "Users" ADD "EmailVerificationTokenHash" character varying(64);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927202033_RequireEmailVerification') THEN
    ALTER TABLE "Users" ADD "EmailVerifiedAt" timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927202033_RequireEmailVerification') THEN
    ALTER TABLE "Users" ADD "RequiresEmailVerification" boolean NOT NULL DEFAULT FALSE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927202033_RequireEmailVerification') THEN
    CREATE UNIQUE INDEX "IX_Users_EmailVerificationTokenHash" ON "Users" ("EmailVerificationTokenHash");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927202033_RequireEmailVerification') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260927202033_RequireEmailVerification', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927204923_PersistTelemetryConnectionOwner') THEN
    ALTER TABLE "MachineConnectionStates" ADD "OwnerNodeId" character varying(32);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927204923_PersistTelemetryConnectionOwner') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260927204923_PersistTelemetryConnectionOwner', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927210225_RouteTelemetryCommandsToOwner') THEN
    CREATE TABLE "TelemetryCommandDispatches" (
        "Id" uuid NOT NULL,
        "CompanyId" uuid NOT NULL,
        "MachineId" text NOT NULL,
        "OwnerNodeId" character varying(32) NOT NULL,
        "PayloadJson" text NOT NULL,
        "Status" character varying(24) NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "ProcessingStartedAt" timestamp with time zone,
        "CompletedAt" timestamp with time zone,
        "MessageId" integer,
        "Attempts" integer,
        "Success" boolean,
        "Error" character varying(100),
        CONSTRAINT "PK_TelemetryCommandDispatches" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927210225_RouteTelemetryCommandsToOwner') THEN
    CREATE INDEX "IX_TelemetryCommandDispatches_OwnerNodeId_Status_CreatedAt" ON "TelemetryCommandDispatches" ("OwnerNodeId", "Status", "CreatedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927210225_RouteTelemetryCommandsToOwner') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260927210225_RouteTelemetryCommandsToOwner', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927212400_RouteMdbStatusToOwner') THEN
    ALTER TABLE "TelemetryCommandDispatches" ADD "Operation" character varying(24) NOT NULL DEFAULT 'Command';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927212400_RouteMdbStatusToOwner') THEN
    ALTER TABLE "TelemetryCommandDispatches" ADD "ResponseAt" timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927212400_RouteMdbStatusToOwner') THEN
    ALTER TABLE "TelemetryCommandDispatches" ADD "ResponseDataJson" text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927212400_RouteMdbStatusToOwner') THEN
    ALTER TABLE "TelemetryCommandDispatches" ADD "ResponseStatus" character varying(50);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927212400_RouteMdbStatusToOwner') THEN
    ALTER TABLE "TelemetryCommandDispatches" ADD "TimeoutMilliseconds" integer NOT NULL DEFAULT 15000;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927212400_RouteMdbStatusToOwner') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260927212400_RouteMdbStatusToOwner', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927215538_ClearStaleTelemetryOwnerOnLegacyReconnect') THEN
    ALTER TABLE "MachineConnectionStates" ADD "OwnerConnectionId" character varying(32);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927215538_ClearStaleTelemetryOwnerOnLegacyReconnect') THEN
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
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260927215538_ClearStaleTelemetryOwnerOnLegacyReconnect') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260927215538_ClearStaleTelemetryOwnerOnLegacyReconnect', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261025150422_AddTelemetryCommandRoutingCapability') THEN
    ALTER TABLE "MachineConnectionStates" ADD "CommandRoutingVersion" integer;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261025150422_AddTelemetryCommandRoutingCapability') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261025150422_AddTelemetryCommandRoutingCapability', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261025180955_AddSessionAdmissionGate') THEN
    CREATE TABLE "SessionAdmissionGates" (
        "Id" integer GENERATED BY DEFAULT AS IDENTITY,
        "NewSessionsPaused" boolean NOT NULL,
        "UpdatedAt" timestamp with time zone NOT NULL,
        "UpdatedBy" character varying(128) NOT NULL,
        "Reason" character varying(256) NOT NULL,
        CONSTRAINT "PK_SessionAdmissionGates" PRIMARY KEY ("Id"),
        CONSTRAINT "CK_SessionAdmissionGates_Singleton" CHECK ("Id" = 1)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261025180955_AddSessionAdmissionGate') THEN
    INSERT INTO "SessionAdmissionGates" ("Id", "NewSessionsPaused", "UpdatedAt", "UpdatedBy", "Reason")
    VALUES (1, FALSE, TIMESTAMPTZ '2026-10-25T18:09:55Z', 'migration', 'default');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261025180955_AddSessionAdmissionGate') THEN
    CREATE FUNCTION "RejectMachineSessionWhenDeploymentPaused"()
    RETURNS trigger
    LANGUAGE plpgsql
    AS $$
    DECLARE admission_paused boolean;
    BEGIN
        SELECT "NewSessionsPaused" INTO admission_paused
        FROM "SessionAdmissionGates"
        WHERE "Id" = 1
        FOR SHARE;

        IF admission_paused IS DISTINCT FROM FALSE THEN
            RAISE EXCEPTION 'deployment_draining' USING ERRCODE = 'P0001';
        END IF;

        RETURN NEW;
    END;
    $$;

    CREATE TRIGGER "TR_MachineSessions_RejectWhenDeploymentPaused"
    BEFORE INSERT ON "MachineSessions"
    FOR EACH ROW
    EXECUTE FUNCTION "RejectMachineSessionWhenDeploymentPaused"();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261025180955_AddSessionAdmissionGate') THEN
    PERFORM setval(
        pg_get_serial_sequence('"SessionAdmissionGates"', 'Id'),
        GREATEST(
            (SELECT MAX("Id") FROM "SessionAdmissionGates") + 1,
            nextval(pg_get_serial_sequence('"SessionAdmissionGates"', 'Id'))),
        false);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261025180955_AddSessionAdmissionGate') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261025180955_AddSessionAdmissionGate', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261025185728_AddTelemetryCommandResolution') THEN
    CREATE TABLE "TelemetryCommandResolutions" (
        "Id" uuid NOT NULL,
        "TelemetryCommandId" uuid,
        "TelemetryCommandDispatchId" uuid,
        "OperatorName" character varying(128) NOT NULL,
        "Reason" character varying(512) NOT NULL,
        "EvidenceReference" character varying(512) NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_TelemetryCommandResolutions" PRIMARY KEY ("Id"),
        CONSTRAINT "CK_TelemetryCommandResolutions_OneCommand" CHECK (("TelemetryCommandId" IS NOT NULL AND "TelemetryCommandDispatchId" IS NULL) OR ("TelemetryCommandId" IS NULL AND "TelemetryCommandDispatchId" IS NOT NULL)),
        CONSTRAINT "FK_TelemetryCommandResolutions_TelemetryCommands_TelemetryComm~" FOREIGN KEY ("TelemetryCommandId") REFERENCES "TelemetryCommands" ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261025185728_AddTelemetryCommandResolution') THEN
    CREATE UNIQUE INDEX "IX_TelemetryCommandResolutions_TelemetryCommandDispatchId" ON "TelemetryCommandResolutions" ("TelemetryCommandDispatchId") WHERE "TelemetryCommandDispatchId" IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261025185728_AddTelemetryCommandResolution') THEN
    CREATE UNIQUE INDEX "IX_TelemetryCommandResolutions_TelemetryCommandId" ON "TelemetryCommandResolutions" ("TelemetryCommandId") WHERE "TelemetryCommandId" IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261025185728_AddTelemetryCommandResolution') THEN
    CREATE FUNCTION "ValidateTelemetryCommandResolutionTarget"()
    RETURNS trigger
    LANGUAGE plpgsql
    AS $$
    DECLARE
        command_status text;
        command_error text;
        dispatch_status text;
        dispatch_operation text;
        dispatch_error text;
    BEGIN
        IF length(btrim(NEW."OperatorName")) = 0 OR
           length(btrim(NEW."Reason")) = 0 OR
           length(btrim(NEW."EvidenceReference")) = 0 THEN
            RAISE EXCEPTION 'telemetry_command_resolution_details_required' USING ERRCODE = 'P0001';
        END IF;

        IF NEW."TelemetryCommandId" IS NOT NULL THEN
            SELECT command."Status", command."Error"
            INTO command_status, command_error
            FROM "TelemetryCommands" command
            WHERE command."Id" = NEW."TelemetryCommandId";

            IF NOT FOUND OR command_status IS DISTINCT FROM 'failed' OR
               command_error IS NULL OR
               command_error NOT IN ('ack_timeout', 'command_outcome_unknown') THEN
                RAISE EXCEPTION 'telemetry_command_not_unresolved' USING ERRCODE = 'P0001';
            END IF;
        ELSE
            SELECT dispatch."Status", dispatch."Operation", dispatch."Error"
            INTO dispatch_status, dispatch_operation, dispatch_error
            FROM "TelemetryCommandDispatches" dispatch
            WHERE dispatch."Id" = NEW."TelemetryCommandDispatchId";

            IF NOT FOUND OR dispatch_operation IS NULL OR dispatch_operation = 'MdbStatus' OR
               (dispatch_status <> 'Indeterminate' AND
                NOT (dispatch_status = 'Failed' AND dispatch_error IS NOT NULL AND
                     dispatch_error IN ('ack_timeout', 'command_outcome_unknown')) AND
                NOT (dispatch_status = 'Cancelled' AND dispatch_error = 'cancelled_before_dispatch')) THEN
                RAISE EXCEPTION 'telemetry_command_not_unresolved' USING ERRCODE = 'P0001';
            END IF;
        END IF;

        RETURN NEW;
    END;
    $$;

    CREATE TRIGGER "TR_TelemetryCommandResolutions_ValidateTarget"
    BEFORE INSERT ON "TelemetryCommandResolutions"
    FOR EACH ROW
    EXECUTE FUNCTION "ValidateTelemetryCommandResolutionTarget"();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261025185728_AddTelemetryCommandResolution') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261025185728_AddTelemetryCommandResolution', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261025193143_AddPaymentReconciliationReason') THEN
    ALTER TABLE "PaymentTransactions" ADD "ReconciliationReason" character varying(128);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261025193143_AddPaymentReconciliationReason') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261025193143_AddPaymentReconciliationReason', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026032226_AddPrivacyPolicyVersion') THEN
    ALTER TABLE "Users" ADD "PrivacyPolicyVersion" character varying(32);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026032226_AddPrivacyPolicyVersion') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261026032226_AddPrivacyPolicyVersion', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026035921_BackfillLegacyDeliveredSales') THEN
    WITH legacy_sales AS (
        SELECT DISTINCT ON (session."Id")
            session."CompanyId",
            session."MachineId",
            session."Id" AS "SessionId",
            tx."Id" AS "TransactionId",
            session."ItemNumber",
            tx."Amount" AS "GrossAmount",
            tx."ApplicationFee",
            tx."Amount" - tx."ApplicationFee" AS "NetAmount",
            telemetry."CreatedAt" AS "SoldAt"
        FROM "TelemetryEvents" AS telemetry
        INNER JOIN "MachineSessions" AS session ON session."Id" = telemetry."SessionId"
        INNER JOIN "PaymentTransactions" AS tx ON tx."Id" = telemetry."TransactionId"
        WHERE telemetry."EventType" = 'vend.success'
          AND telemetry."SessionId" IS NOT NULL
          AND telemetry."TransactionId" IS NOT NULL
          AND session."TransactionId" = tx."Id"
          AND session."CompanyId" = tx."CompanyId"
          AND session."MachineId" = tx."MachineId"
          AND telemetry."CompanyId" = session."CompanyId"
          AND telemetry."MachineId" = session."MachineId"
          AND session."ItemNumber" IS NOT NULL
          AND tx."Status" = 'Approved'
        ORDER BY session."Id", telemetry."CreatedAt" ASC
    )
    INSERT INTO "SaleRecords" (
        "Id", "CompanyId", "MachineId", "SessionId", "TransactionId",
        "ItemNumber", "ProductId", "ProductNameSnapshot", "Quantity",
        "GrossAmount", "ApplicationFee", "NetAmount", "Origin", "SoldAt"
    )
    SELECT md5("SessionId"::text || ':' || "TransactionId"::text)::uuid,
           "CompanyId", "MachineId", "SessionId", "TransactionId",
           "ItemNumber", NULL, NULL, 1, "GrossAmount", "ApplicationFee", "NetAmount",
           'LegacyBackfill', "SoldAt"
    FROM legacy_sales
    ON CONFLICT DO NOTHING;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026035921_BackfillLegacyDeliveredSales') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261026035921_BackfillLegacyDeliveredSales', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026075639_EnforceSystemSettingsSingleton') THEN
    ALTER TABLE "SystemSettings" ADD "IsSingleton" boolean NOT NULL DEFAULT TRUE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026075639_EnforceSystemSettingsSingleton') THEN
    CREATE UNIQUE INDEX "IX_SystemSettings_IsSingleton" ON "SystemSettings" ("IsSingleton");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026075639_EnforceSystemSettingsSingleton') THEN
    ALTER TABLE "SystemSettings" ADD CONSTRAINT "CK_SystemSettings_Singleton" CHECK ("IsSingleton" = TRUE);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026075639_EnforceSystemSettingsSingleton') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261026075639_EnforceSystemSettingsSingleton', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026082311_AddSaleLocationSnapshot') THEN
    ALTER TABLE "SaleRecords" ADD "LocationIdSnapshot" uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026082311_AddSaleLocationSnapshot') THEN
    ALTER TABLE "SaleRecords" ADD "LocationNameSnapshot" text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026082311_AddSaleLocationSnapshot') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261026082311_AddSaleLocationSnapshot', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026082904_AddSessionLocationSnapshot') THEN
    ALTER TABLE "MachineSessions" ADD "LocationIdSnapshot" uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026082904_AddSessionLocationSnapshot') THEN
    ALTER TABLE "MachineSessions" ADD "LocationNameSnapshot" text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026082904_AddSessionLocationSnapshot') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261026082904_AddSessionLocationSnapshot', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026083333_AddSessionProductSnapshot') THEN
    ALTER TABLE "MachineSessions" ADD "SelectedProductIdSnapshot" text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026083333_AddSessionProductSnapshot') THEN
    ALTER TABLE "MachineSessions" ADD "SelectedProductNameSnapshot" text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261026083333_AddSessionProductSnapshot') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261026083333_AddSessionProductSnapshot', '10.0.12');
    END IF;
END $EF$;
COMMIT;

