-- Consulta somente leitura. Execute com acesso operacional autorizado e depois
-- de confirmar que o schema inclui MachineSessions.State, TelemetryCommandDispatches.Operation,
-- TelemetryCommands, TelemetryCommandResolutions, DeviceMessageInbox,
-- WebhookInboxMessages, OutboxMessages, SystemSettings e SessionAdmissionGates.
-- Não retorna dados de cliente, máquina,
-- pagamento ou credencial. A consulta é somente leitura: não pause sessões para liberá-la.
BEGIN TRANSACTION READ ONLY;
SET LOCAL statement_timeout = '5s';
SET LOCAL TIME ZONE 'UTC';

SELECT
    count(*)::bigint AS active_vend_sessions,
    min("StartedAt") AS oldest_active_session_started_at_utc,
    coalesce(
        floor(extract(epoch FROM (now() - min("StartedAt")))),
        0
    )::bigint AS oldest_active_session_age_seconds
FROM "MachineSessions"
WHERE "ClosedAt" IS NULL;

SELECT
    count(*)::bigint AS unresolved_session_closures
FROM "MachineSessions"
WHERE "State" = 'ClosureIncomplete';

SELECT
    (
        SELECT count(*)::bigint
        FROM "TelemetryCommandDispatches" dispatch
        WHERE dispatch."Status" IN ('Pending', 'Processing')
           OR (dispatch."Operation" <> 'MdbStatus'
               AND (dispatch."Status" = 'Indeterminate' OR
                    (dispatch."Status" = 'Failed' AND dispatch."Error" IN ('ack_timeout', 'command_outcome_unknown')))
               AND NOT EXISTS (
                   SELECT 1 FROM "TelemetryCommandResolutions" resolution
                   WHERE resolution."TelemetryCommandDispatchId" = dispatch."Id"
               ))
    ) + (
        SELECT count(*)::bigint
        FROM "TelemetryCommands" command
        WHERE (command."Status" = 'accepted' AND command."Error" = 'command_intent_persisted')
           OR (command."Status" = 'failed'
               AND command."Error" IN ('ack_timeout', 'command_outcome_unknown')
               AND NOT EXISTS (
                   SELECT 1 FROM "TelemetryCommandResolutions" resolution
                   WHERE resolution."TelemetryCommandId" = command."Id"
               ))
    ) AS unresolved_machine_commands;

SELECT
    CASE
        WHEN (SELECT count("ApplicationFeePercent") FROM "SystemSettings") <> 1
          OR EXISTS (
              SELECT 1 FROM "SystemSettings"
              WHERE "ApplicationFeePercent" <= 0 OR "ApplicationFeePercent" >= 100
          )
        THEN 1::bigint
        ELSE 0::bigint
    END AS invalid_platform_fee_settings;

SELECT
    count(*)::bigint AS unprocessed_device_messages
FROM "DeviceMessageInbox"
WHERE "Status" <> 'Processed';

SELECT
    count(*)::bigint AS unprocessed_payment_webhooks
FROM "WebhookInboxMessages"
WHERE "Status" <> 'Processed';

SELECT
    count(*)::bigint AS financial_outbox_in_flight
FROM "OutboxMessages"
WHERE "Status" = 'processing';

SELECT
    CASE
        WHEN EXISTS (
            SELECT 1 FROM "SessionAdmissionGates"
            WHERE "Id" = 1 AND "NewSessionsPaused"
        ) THEN 0::bigint
        ELSE 1::bigint
    END AS session_admission_open;

ROLLBACK;
