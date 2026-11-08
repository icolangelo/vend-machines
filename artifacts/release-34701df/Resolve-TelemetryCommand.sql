\set ON_ERROR_STOP on
\unset resolved_target_id
\if :{?command_id}
\else
    \set command_id
\endif
\if :{?dispatch_id}
\else
    \set dispatch_id
\endif
\if :{?operator_name}
\else
    \echo 'Informe -v operator_name=<identificação do operador>.'
    DO $$ BEGIN RAISE EXCEPTION 'operator_name_required'; END $$;
\endif
\if :{?resolution_reason}
\else
    \echo 'Informe -v resolution_reason=<resultado da conferência física>.'
    DO $$ BEGIN RAISE EXCEPTION 'resolution_reason_required'; END $$;
\endif
\if :{?evidence_reference}
\else
    \echo 'Informe -v evidence_reference=<chamado ou referência operacional>.'
    DO $$ BEGIN RAISE EXCEPTION 'evidence_reference_required'; END $$;
\endif

SELECT
    (NULLIF(:'command_id', '') IS NOT NULL) <>
    (NULLIF(:'dispatch_id', '') IS NOT NULL) AS one_target,
    length(btrim(:'operator_name')) > 0 AS operator_name_valid,
    length(btrim(:'resolution_reason')) > 0 AS resolution_reason_valid,
    length(btrim(:'evidence_reference')) > 0 AS evidence_reference_valid
\gset
\if :one_target
\else
    DO $$ BEGIN RAISE EXCEPTION 'exactly_one_command_target_required'; END $$;
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

SELECT
    EXISTS (
        SELECT 1 FROM "TelemetryCommands" command
        WHERE command."Id" = NULLIF(:'command_id', '')::uuid
          AND (
              (command."Status" = 'accepted' AND command."Error" = 'command_intent_persisted'
               AND command."CreatedAt" <= NOW() - INTERVAL '5 minutes')
              OR (command."Status" = 'failed'
                  AND command."Error" IN ('ack_timeout', 'command_outcome_unknown'))
          )
          AND NOT EXISTS (
              SELECT 1 FROM "TelemetryCommandResolutions" resolution
              WHERE resolution."TelemetryCommandId" = command."Id"
          )
    ) OR EXISTS (
        SELECT 1 FROM "TelemetryCommandDispatches" dispatch
        WHERE dispatch."Id" = NULLIF(:'dispatch_id', '')::uuid
          AND dispatch."Operation" <> 'MdbStatus'
          AND (dispatch."Status" = 'Indeterminate' OR
               (dispatch."Status" = 'Failed' AND dispatch."Error" IN ('ack_timeout', 'command_outcome_unknown')))
          AND NOT EXISTS (
              SELECT 1 FROM "TelemetryCommandResolutions" resolution
              WHERE resolution."TelemetryCommandDispatchId" = dispatch."Id"
          )
    ) AS command_is_unresolved
\gset

\if :command_is_unresolved
    -- Intenções ainda em execução não podem ser reconciliadas. Só converta uma
    -- intenção órfã depois de cinco minutos sem finalização.
    UPDATE "TelemetryCommands"
    SET "Status" = 'failed',
        "Error" = 'command_outcome_unknown'
    WHERE NULLIF(:'command_id', '') IS NOT NULL
      AND "Id" = NULLIF(:'command_id', '')::uuid
      AND "Status" = 'accepted'
      AND "Error" = 'command_intent_persisted'
      AND "CreatedAt" <= NOW() - INTERVAL '5 minutes'
      AND NOT EXISTS (
          SELECT 1 FROM "TelemetryCommandResolutions" resolution
          WHERE resolution."TelemetryCommandId" = "TelemetryCommands"."Id"
      );

    INSERT INTO "TelemetryCommandResolutions" (
        "Id", "TelemetryCommandId", "TelemetryCommandDispatchId",
        "OperatorName", "Reason", "EvidenceReference", "CreatedAt"
    )
    SELECT
        gen_random_uuid(),
        command."Id",
        NULL::uuid,
        :'operator_name',
        :'resolution_reason',
        :'evidence_reference',
        NOW()
    FROM "TelemetryCommands" command
    WHERE NULLIF(:'command_id', '') IS NOT NULL
      AND command."Id" = NULLIF(:'command_id', '')::uuid
      AND command."Status" = 'failed'
      AND command."Error" IN ('ack_timeout', 'command_outcome_unknown')
      AND NOT EXISTS (
          SELECT 1 FROM "TelemetryCommandResolutions" resolution
          WHERE resolution."TelemetryCommandId" = command."Id"
      )
    UNION ALL
    SELECT
        gen_random_uuid(),
        NULL::uuid,
        dispatch."Id",
        :'operator_name',
        :'resolution_reason',
        :'evidence_reference',
        NOW()
    FROM "TelemetryCommandDispatches" dispatch
    WHERE NULLIF(:'dispatch_id', '') IS NOT NULL
      AND dispatch."Id" = NULLIF(:'dispatch_id', '')::uuid
      AND dispatch."Operation" <> 'MdbStatus'
      AND (dispatch."Status" = 'Indeterminate' OR
           (dispatch."Status" = 'Failed' AND dispatch."Error" IN ('ack_timeout', 'command_outcome_unknown')))
      AND NOT EXISTS (
          SELECT 1 FROM "TelemetryCommandResolutions" resolution
          WHERE resolution."TelemetryCommandDispatchId" = dispatch."Id"
      )
    RETURNING COALESCE("TelemetryCommandId", "TelemetryCommandDispatchId") AS resolved_target_id \gset
    \if :{?resolved_target_id}
        COMMIT;
        SELECT :'resolved_target_id'::uuid AS resolved_target_id;
    \else
        ROLLBACK;
        DO $$ BEGIN RAISE EXCEPTION 'telemetry_command_changed_before_resolution'; END $$;
    \endif
\else
    \echo 'Comando inexistente, já reconciliado ou sem resultado MDB incerto; nenhuma alteração realizada.'
    ROLLBACK;
    DO $$ BEGIN RAISE EXCEPTION 'telemetry_command_not_unresolved'; END $$;
\endif
