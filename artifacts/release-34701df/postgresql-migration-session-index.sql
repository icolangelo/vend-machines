SET lock_timeout = '5s';
START TRANSACTION;
ALTER TABLE "Machines"
    ADD COLUMN IF NOT EXISTS "AutoOpenSessionEnabled" boolean NOT NULL DEFAULT FALSE;
ALTER TABLE "MachineConnectionStates"
    ADD COLUMN IF NOT EXISTS "AutoOpenLastAttemptAt" timestamp with time zone NULL;
ALTER TABLE "MachineConnectionStates"
    ADD COLUMN IF NOT EXISTS "AutoOpenLastError" text NULL;
ALTER TABLE "MachineConnectionStates"
    ADD COLUMN IF NOT EXISTS "MdbStatus" text NULL;
ALTER TABLE "MachineConnectionStates"
    ADD COLUMN IF NOT EXISTS "MdbStatusRaw" text NULL;
ALTER TABLE "MachineConnectionStates"
    ADD COLUMN IF NOT EXISTS "MdbStatusRequestAt" timestamp with time zone NULL;
ALTER TABLE "MachineConnectionStates"
    ADD COLUMN IF NOT EXISTS "MdbStatusRequestMessageId" integer NULL;
ALTER TABLE "MachineConnectionStates"
    ADD COLUMN IF NOT EXISTS "MdbStatusUpdatedAt" timestamp with time zone NULL;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM "MachineSessions"
        WHERE "ClosedAt" IS NULL
        GROUP BY "MachineId"
        HAVING COUNT(*) > 1
    ) THEN
        RAISE EXCEPTION 'Existem máquinas com mais de uma sessão aberta. Corrija os dados antes de aplicar a migration.';
    END IF;
END $$;

COMMIT;

CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS "UX_MachineSessions_OneOpenPerMachine"
ON "MachineSessions" ("MachineId")
WHERE "ClosedAt" IS NULL;

DO $$
DECLARE index_is_valid boolean;
BEGIN
    SELECT i.indisvalid AND i.indisready AND i.indisunique
           AND i.indnkeyatts = 1
           AND pg_get_indexdef(i.indexrelid) LIKE '%USING btree ("MachineId") WHERE ("ClosedAt" IS NULL)'
      INTO index_is_valid
      FROM pg_index i
      JOIN pg_class index_relation ON index_relation.oid = i.indexrelid
      JOIN pg_namespace index_schema ON index_schema.oid = index_relation.relnamespace
     WHERE index_relation.relname = 'UX_MachineSessions_OneOpenPerMachine'
       AND index_schema.nspname = current_schema();

    IF COALESCE(index_is_valid, FALSE) IS NOT TRUE THEN
        RAISE EXCEPTION 'O índice UX_MachineSessions_OneOpenPerMachine está ausente ou inválido; remova-o com DROP INDEX CONCURRENTLY e repita a migration.';
    END IF;
END $$;

DROP INDEX CONCURRENTLY IF EXISTS "IX_MachineSessions_MachineId";

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260730120000_AddMdbStatusAndAutomaticSession', '10.0.12');

