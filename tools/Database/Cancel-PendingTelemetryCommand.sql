\set ON_ERROR_STOP on
\unset cancelled_dispatch_id
-- Permissões mínimas: SELECT em Id/Operation/Status/Error de dispatch;
-- UPDATE em Status/Error/CompletedAt; INSERT na resolução e SELECT em
-- TelemetryCommandResolutions.TelemetryCommandDispatchId para o RETURNING.
\if :{?dispatch_id}
\else
    \echo 'Informe -v dispatch_id=<UUID do dispatch>.'
    DO $$ BEGIN RAISE EXCEPTION 'dispatch_id_required'; END $$;
\endif
\if :{?operator_name}
\else
    \echo 'Informe -v operator_name=<identificação do operador>.'
    DO $$ BEGIN RAISE EXCEPTION 'operator_name_required'; END $$;
\endif
\if :{?resolution_reason}
\else
    \echo 'Informe -v resolution_reason=<motivo do cancelamento antes do envio>.'
    DO $$ BEGIN RAISE EXCEPTION 'resolution_reason_required'; END $$;
\endif
\if :{?evidence_reference}
\else
    \echo 'Informe -v evidence_reference=<chamado ou referência operacional>.'
    DO $$ BEGIN RAISE EXCEPTION 'evidence_reference_required'; END $$;
\endif

SELECT
    length(btrim(:'dispatch_id')) > 0 AS dispatch_id_valid,
    length(btrim(:'operator_name')) > 0 AS operator_name_valid,
    length(btrim(:'resolution_reason')) > 0 AS resolution_reason_valid,
    length(btrim(:'evidence_reference')) > 0 AS evidence_reference_valid
\gset
\if :dispatch_id_valid
\else
    DO $$ BEGIN RAISE EXCEPTION 'dispatch_id_required'; END $$;
\endif
\if :operator_name_valid
\else
    DO $$ BEGIN RAISE EXCEPTION 'operator_name_required'; END $$;
\endif
\if :resolution_reason_valid
\else
    DO $$ BEGIN RAISE EXCEPTION 'resolution_reason_required'; END $$;
\endif
\if :evidence_reference_valid
\else
    DO $$ BEGIN RAISE EXCEPTION 'evidence_reference_required'; END $$;
\endif

BEGIN;
SET LOCAL lock_timeout = '5s';

-- The conditional update races safely with the worker's Pending -> Processing
-- claim: exactly one transition wins. Processing is deliberately never reset.
WITH cancelled AS (
    UPDATE "TelemetryCommandDispatches"
    SET "Status" = 'Cancelled',
        "Error" = 'cancelled_before_dispatch',
        "CompletedAt" = NOW()
    WHERE "Id" = :'dispatch_id'::uuid
      AND "Operation" <> 'MdbStatus'
      AND "Status" = 'Pending'
    RETURNING "Id"
)
INSERT INTO "TelemetryCommandResolutions" (
    "Id", "TelemetryCommandId", "TelemetryCommandDispatchId",
    "OperatorName", "Reason", "EvidenceReference", "CreatedAt"
)
SELECT
    gen_random_uuid(), NULL::uuid, cancelled."Id",
    :'operator_name', :'resolution_reason', :'evidence_reference', NOW()
FROM cancelled
RETURNING "TelemetryCommandDispatchId" AS cancelled_dispatch_id
\gset

\if :{?cancelled_dispatch_id}
    COMMIT;
    SELECT :'cancelled_dispatch_id'::uuid AS cancelled_dispatch_id;
\else
    ROLLBACK;
    DO $$ BEGIN RAISE EXCEPTION 'telemetry_dispatch_not_pending'; END $$;
\endif
