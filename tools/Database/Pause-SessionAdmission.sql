\set ON_ERROR_STOP on
\if :{?operator_name}
\else
    \echo 'Informe -v operator_name=<identificação>.'
    DO $$ BEGIN RAISE EXCEPTION 'operator_name_required'; END $$;
\endif
\if :{?pause_reason}
\else
    \echo 'Informe -v pause_reason=<motivo>.'
    DO $$ BEGIN RAISE EXCEPTION 'pause_reason_required'; END $$;
\endif

BEGIN;
SET LOCAL lock_timeout = '15s';

SELECT EXISTS (
    SELECT 1 FROM "SessionAdmissionGates" WHERE "Id" = 1
) AS gate_exists \gset
\if :gate_exists
    WITH updated_gate AS (
        UPDATE "SessionAdmissionGates"
        SET "NewSessionsPaused" = TRUE,
            "UpdatedAt" = NOW(),
            "UpdatedBy" = :'operator_name',
            "Reason" = :'pause_reason'
        WHERE "Id" = 1
        RETURNING 1
    )
    SELECT EXISTS (SELECT 1 FROM updated_gate) AS gate_updated \gset
    \if :gate_updated
    \else
        \echo 'O registro de controle não foi atualizado; nenhuma pausa foi confirmada.'
        ROLLBACK;
        DO $$ BEGIN RAISE EXCEPTION 'session_admission_gate_update_missing'; END $$;
    \endif
\else
    \echo 'Session admission gate ausente; nenhuma alteração realizada.'
    ROLLBACK;
    DO $$ BEGIN RAISE EXCEPTION 'session_admission_gate_unavailable'; END $$;
\endif

COMMIT;

SELECT "NewSessionsPaused", "UpdatedAt", "UpdatedBy", "Reason"
FROM "SessionAdmissionGates"
WHERE "Id" = 1;
