\set ON_ERROR_STOP on
-- Execute somente após as migrations da versão candidata, incluindo CommandRoutingVersion.
\if :{?operator_name}
\else
    \echo 'Informe -v operator_name=<identificação>.'
    DO $$ BEGIN RAISE EXCEPTION 'operator_name_required'; END $$;
\endif
\if :{?resume_reason}
\else
    \echo 'Informe -v resume_reason=<motivo>.'
    DO $$ BEGIN RAISE EXCEPTION 'resume_reason_required'; END $$;
\endif

BEGIN;
SET LOCAL lock_timeout = '15s';

SELECT EXISTS (
    SELECT 1 FROM "SessionAdmissionGates" WHERE "Id" = 1
) AS gate_exists \gset
\if :gate_exists
    SELECT "NewSessionsPaused" AS gate_paused
    FROM "SessionAdmissionGates"
    WHERE "Id" = 1
    FOR UPDATE \gset
    \if :gate_paused
        SELECT
            EXISTS (
                SELECT 1 FROM "MachineSessions" WHERE "ClosedAt" IS NULL
            ) AS active_vend_sessions,
            EXISTS (
                SELECT 1 FROM "MachineSessions" WHERE "State" = 'ClosureIncomplete'
            ) AS unresolved_session_closures,
            EXISTS (
                SELECT 1
                FROM "MachineConnectionStates" state
                WHERE state."IsOnline"
                  AND (state."LastSeenAt" IS NULL OR state."LastSeenAt" >= NOW() - INTERVAL '90 seconds')
                  AND (state."ConnectionId" IS NULL OR state."ConnectionId" = ''
                       OR state."OwnerNodeId" IS NULL OR state."OwnerNodeId" = ''
                       OR state."OwnerConnectionId" IS DISTINCT FROM state."ConnectionId"
                       OR state."CommandRoutingVersion" IS DISTINCT FROM 1
                       OR NOT EXISTS (
                           SELECT 1 FROM "Machines" machine
                           WHERE machine."Id" = state."MachineId"
                             AND machine."CompanyId" = state."CompanyId"
                       ))
            ) AS machine_connections_unroutable,
            EXISTS (
                SELECT 1 FROM "TelemetryCommandDispatches" dispatch
                WHERE dispatch."Status" IN ('Pending', 'Processing')
                   OR (dispatch."Operation" <> 'MdbStatus'
                       AND (dispatch."Status" = 'Indeterminate' OR
                            (dispatch."Status" = 'Failed' AND dispatch."Error" IN ('ack_timeout', 'command_outcome_unknown')))
                       AND NOT EXISTS (
                           SELECT 1 FROM "TelemetryCommandResolutions" resolution
                           WHERE resolution."TelemetryCommandDispatchId" = dispatch."Id"
                       ))
            ) OR EXISTS (
                SELECT 1 FROM "TelemetryCommands" command
                WHERE (command."Status" = 'accepted' AND command."Error" = 'command_intent_persisted')
                   OR (command."Status" = 'failed'
                       AND command."Error" IN ('ack_timeout', 'command_outcome_unknown')
                       AND NOT EXISTS (
                           SELECT 1 FROM "TelemetryCommandResolutions" resolution
                           WHERE resolution."TelemetryCommandId" = command."Id"
                       ))
            ) AS unresolved_machine_commands,
            EXISTS (
                SELECT 1
                WHERE (SELECT count("ApplicationFeePercent") FROM "SystemSettings") <> 1
                   OR EXISTS (
                       SELECT 1 FROM "SystemSettings"
                       WHERE "ApplicationFeePercent" <= 0 OR "ApplicationFeePercent" >= 100
                   )
            ) AS invalid_platform_fee_settings,
            EXISTS (
                SELECT 1 FROM "DeviceMessageInbox"
                WHERE "Status" <> 'Processed'
            ) AS unprocessed_device_messages,
            EXISTS (
                SELECT 1 FROM "WebhookInboxMessages"
                WHERE "Status" <> 'Processed'
            ) AS unprocessed_payment_webhooks,
            EXISTS (
                SELECT 1 FROM "OutboxMessages"
                WHERE "Status" = 'processing'
            ) AS financial_outbox_in_flight
        \gset
        \if :active_vend_sessions
            \echo 'Venda fisica aberta; novas sessoes permanecem pausadas.'
            ROLLBACK;
            DO $$ BEGIN RAISE EXCEPTION 'active_vend_sessions_block_resume'; END $$;
        \endif
        \if :unresolved_session_closures
            \echo 'Encerramento fisico sem confirmacao; novas sessoes permanecem pausadas.'
            ROLLBACK;
            DO $$ BEGIN RAISE EXCEPTION 'unresolved_session_closures_block_resume'; END $$;
        \endif
        \if :machine_connections_unroutable
            \echo 'Conexao MDB online recente sem rota compativel; novas sessoes permanecem pausadas.'
            ROLLBACK;
            DO $$ BEGIN RAISE EXCEPTION 'machine_connections_unroutable_block_resume'; END $$;
        \endif
        \if :unresolved_machine_commands
            \echo 'Comando MDB mutavel nao resolvido; novas sessoes permanecem pausadas.'
            ROLLBACK;
            DO $$ BEGIN RAISE EXCEPTION 'unresolved_machine_command_blocks_resume'; END $$;
        \endif
        \if :invalid_platform_fee_settings
            \echo 'Configuracao global da taxa ausente, duplicada ou invalida; novas sessoes permanecem pausadas.'
            ROLLBACK;
            DO $$ BEGIN RAISE EXCEPTION 'invalid_platform_fee_setting_blocks_resume'; END $$;
        \endif
        \if :unprocessed_device_messages
            \echo 'Mensagens de telemetria nao processadas; novas sessoes permanecem pausadas.'
            ROLLBACK;
            DO $$ BEGIN RAISE EXCEPTION 'unprocessed_device_messages_block_resume'; END $$;
        \endif
        \if :unprocessed_payment_webhooks
            \echo 'Webhooks de pagamento nao processados; novas sessoes permanecem pausadas.'
            ROLLBACK;
            DO $$ BEGIN RAISE EXCEPTION 'unprocessed_payment_webhooks_block_resume'; END $$;
        \endif
        \if :financial_outbox_in_flight
            \echo 'Operacao financeira ainda esta em processamento; novas sessoes permanecem pausadas.'
            ROLLBACK;
            DO $$ BEGIN RAISE EXCEPTION 'financial_outbox_in_flight_blocks_resume'; END $$;
        \endif
    \else
        \echo 'Admissao ja estava aberta; nenhuma alteracao realizada.'
        ROLLBACK;
        DO $$ BEGIN RAISE EXCEPTION 'session_admission_already_open'; END $$;
    \endif
    UPDATE "SessionAdmissionGates"
    SET "NewSessionsPaused" = FALSE,
        "UpdatedAt" = NOW(),
        "UpdatedBy" = :'operator_name',
        "Reason" = :'resume_reason'
    WHERE "Id" = 1;
\else
    \echo 'Session admission gate ausente; nenhuma alteração realizada.'
    ROLLBACK;
    DO $$ BEGIN RAISE EXCEPTION 'session_admission_gate_unavailable'; END $$;
\endif

COMMIT;

SELECT "NewSessionsPaused", "UpdatedAt", "UpdatedBy", "Reason"
FROM "SessionAdmissionGates"
WHERE "Id" = 1;
