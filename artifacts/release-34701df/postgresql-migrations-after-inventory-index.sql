SET lock_timeout = '5s';
START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261101221552_AddTelemetryConnectionOwnerLease') THEN
    ALTER TABLE "MachineConnectionStates" ADD "OwnerLeaseExpiresAt" timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261101221552_AddTelemetryConnectionOwnerLease') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261101221552_AddTelemetryConnectionOwnerLease', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261102215805_ClearLegacyTelemetryOwnerRoutingMetadata') THEN
    CREATE OR REPLACE FUNCTION clear_legacy_telemetry_owner_on_connection_change()
    RETURNS trigger
    LANGUAGE plpgsql
    AS $$
    BEGIN
        IF NEW."ConnectionId" IS DISTINCT FROM OLD."ConnectionId"
           AND NEW."OwnerConnectionId" IS NOT DISTINCT FROM OLD."OwnerConnectionId" THEN
            NEW."OwnerNodeId" := NULL;
            NEW."OwnerConnectionId" := NULL;
            NEW."CommandRoutingVersion" := NULL;
            NEW."OwnerLeaseExpiresAt" := NULL;
        END IF;
        RETURN NEW;
    END;
    $$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261102215805_ClearLegacyTelemetryOwnerRoutingMetadata') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261102215805_ClearLegacyTelemetryOwnerRoutingMetadata', '10.0.12');
    END IF;
END $EF$;
COMMIT;

