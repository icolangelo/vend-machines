-- Release 34701df: cinco migrations. Limites baseados no preflight concluído em até 10 segundos e em três vendas elegíveis.
-- Mantenha a API sem aceitar operações durante a aplicação. Pare ao primeiro erro.
SET lock_timeout = '5s';
SET statement_timeout = '10s';
START TRANSACTION;
DO $preflight$
DECLARE legacy_report record; scope_report record;
BEGIN
    IF (SELECT MAX("MigrationId") FROM "__EFMigrationsHistory") IS DISTINCT FROM '20261026032226_AddPrivacyPolicyVersion' THEN
        RAISE EXCEPTION 'O histórico mudou desde a primeira etapa. Pare e confira antes de continuar.';
    END IF;
    IF EXISTS (SELECT 1 FROM "MachineSessions" WHERE "ClosedAt" IS NULL) THEN
        RAISE EXCEPTION 'Há sessões abertas; confira a operação antes da atualização.';
    END IF;
    IF (SELECT COUNT(*) FROM "SystemSettings") <> 1 THEN
        RAISE EXCEPTION 'A configuração global de taxa deixou de ser única.';
    END IF;
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
SELECT count(*) AS eligible_legacy_sales,
       count(*) FILTER (WHERE EXISTS (
           SELECT 1
           FROM "SaleRecords" AS existing
           WHERE existing."SessionId" = legacy."SessionId"
             AND existing."TransactionId" = legacy."TransactionId"
             AND existing."CompanyId" = legacy."CompanyId"
             AND existing."MachineId" = legacy."MachineId"
             AND existing."ItemNumber" = legacy."ItemNumber"
             AND existing."Quantity" = 1
             AND existing."GrossAmount" = legacy."GrossAmount"
             AND existing."ApplicationFee" = legacy."ApplicationFee"
             AND existing."NetAmount" = legacy."NetAmount"
       )) AS already_materialized_sales,
       count(*) FILTER (WHERE
           (EXISTS (
               SELECT 1
               FROM "SaleRecords" AS existing
               WHERE existing."SessionId" = legacy."SessionId"
                  OR existing."TransactionId" = legacy."TransactionId"
                  OR existing."Id" = md5(legacy."SessionId"::text || ':' || legacy."TransactionId"::text)::uuid
           ))
           AND NOT EXISTS (
               SELECT 1
               FROM "SaleRecords" AS existing
               WHERE existing."SessionId" = legacy."SessionId"
                 AND existing."TransactionId" = legacy."TransactionId"
                 AND existing."CompanyId" = legacy."CompanyId"
                 AND existing."MachineId" = legacy."MachineId"
                 AND existing."ItemNumber" = legacy."ItemNumber"
                 AND existing."Quantity" = 1
                 AND existing."GrossAmount" = legacy."GrossAmount"
                 AND existing."ApplicationFee" = legacy."ApplicationFee"
                 AND existing."NetAmount" = legacy."NetAmount"
           )
       ) AS conflicting_existing_sales,
       count(*) FILTER (WHERE NOT EXISTS (
           SELECT 1
           FROM "SaleRecords" AS existing
           WHERE existing."SessionId" = legacy."SessionId"
              OR existing."TransactionId" = legacy."TransactionId"
              OR existing."Id" = md5(legacy."SessionId"::text || ':' || legacy."TransactionId"::text)::uuid
       )) AS anticipated_new_sales
INTO legacy_report
FROM legacy_sales AS legacy;
    IF legacy_report.eligible_legacy_sales <> 3 OR legacy_report.already_materialized_sales <> 0
       OR legacy_report.conflicting_existing_sales <> 0 OR legacy_report.anticipated_new_sales <> 3 THEN
        RAISE EXCEPTION 'O conjunto das vendas mudou desde o preflight; pare e confira os dados.';
    END IF;
SELECT count(*) FILTER (
           WHERE telemetry."SessionId" IS NULL
       ) AS events_without_session_id,
       count(*) FILTER (
           WHERE telemetry."SessionId" IS NOT NULL AND session."Id" IS NULL
       ) AS events_with_unknown_session,
       count(*) FILTER (
           WHERE telemetry."TransactionId" IS NULL
       ) AS events_without_transaction_id,
       count(*) FILTER (
           WHERE telemetry."TransactionId" IS NOT NULL AND tx."Id" IS NULL
       ) AS events_with_unknown_transaction,
       count(*) FILTER (
           WHERE session."Id" IS NOT NULL AND session."ItemNumber" IS NULL
       ) AS events_without_session_item,
       count(*) FILTER (
           WHERE tx."Id" IS NOT NULL AND tx."Status" <> 'Approved'
       ) AS events_with_non_approved_transaction,
       count(*) FILTER (
           WHERE session."Id" IS NOT NULL
             AND tx."Id" IS NOT NULL
             AND session."TransactionId" = tx."Id"
             AND session."CompanyId" = tx."CompanyId"
             AND session."MachineId" = tx."MachineId"
             AND telemetry."CompanyId" = session."CompanyId"
             AND telemetry."MachineId" = session."MachineId"
             AND session."ItemNumber" IS NOT NULL
             AND tx."Status" = 'Approved'
       ) AS eligible_vend_success_events,
       count(DISTINCT session."Id") FILTER (
           WHERE session."ItemNumber" IS NOT NULL
             AND tx."Status" = 'Approved'
             AND session."TransactionId" IS DISTINCT FROM tx."Id"
       ) AS transaction_scope_mismatches,
       count(DISTINCT session."Id") FILTER (
           WHERE session."ItemNumber" IS NOT NULL
             AND tx."Status" = 'Approved'
             AND session."CompanyId" IS DISTINCT FROM tx."CompanyId"
       ) AS company_scope_mismatches,
       count(DISTINCT session."Id") FILTER (
           WHERE session."ItemNumber" IS NOT NULL
             AND tx."Status" = 'Approved'
             AND session."MachineId" IS DISTINCT FROM tx."MachineId"
       ) AS machine_scope_mismatches,
       count(DISTINCT session."Id") FILTER (
           WHERE session."Id" IS NOT NULL
             AND session."ItemNumber" IS NOT NULL
             AND tx."Status" = 'Approved'
             AND telemetry."CompanyId" IS DISTINCT FROM session."CompanyId"
       ) AS telemetry_company_scope_mismatches,
       count(DISTINCT session."Id") FILTER (
           WHERE session."Id" IS NOT NULL
             AND session."ItemNumber" IS NOT NULL
             AND tx."Status" = 'Approved'
             AND telemetry."MachineId" IS DISTINCT FROM session."MachineId"
       ) AS telemetry_machine_scope_mismatches
INTO scope_report
FROM "TelemetryEvents" AS telemetry
LEFT JOIN "MachineSessions" AS session ON session."Id" = telemetry."SessionId"
LEFT JOIN "PaymentTransactions" AS tx ON tx."Id" = telemetry."TransactionId"
WHERE telemetry."EventType" = 'vend.success';
    IF scope_report.transaction_scope_mismatches <> 0 OR scope_report.company_scope_mismatches <> 0
       OR scope_report.machine_scope_mismatches <> 0 OR scope_report.telemetry_company_scope_mismatches <> 0
       OR scope_report.telemetry_machine_scope_mismatches <> 0 THEN
        RAISE EXCEPTION 'Há divergências de empresa, máquina ou transação; o backfill não será executado.';
    END IF;
END;
$preflight$;


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


SELECT COUNT(*) AS migrations_registradas, MAX("MigrationId") AS ultima_migration FROM "__EFMigrationsHistory";
SELECT COUNT(*) AS vendas_backfill FROM "SaleRecords" WHERE "Origin" = 'LegacyBackfill';
