\set ON_ERROR_STOP on
\pset pager off

BEGIN TRANSACTION READ ONLY;
SET LOCAL statement_timeout = '5s';

SELECT source, command_id, status, result_code, owner_node_id
FROM (
    SELECT
        'remote_dispatch'::text AS source,
        dispatch."Id"::text AS command_id,
        dispatch."Status" AS status,
        COALESCE(dispatch."Error", dispatch."Operation") AS result_code,
        dispatch."OwnerNodeId" AS owner_node_id
    FROM "TelemetryCommandDispatches" dispatch
    WHERE dispatch."Status" IN ('Pending', 'Processing')
       OR (dispatch."Operation" <> 'MdbStatus'
           AND (dispatch."Status" = 'Indeterminate' OR
                (dispatch."Status" = 'Failed' AND dispatch."Error" IN ('ack_timeout', 'command_outcome_unknown')))
           AND NOT EXISTS (
               SELECT 1 FROM "TelemetryCommandResolutions" resolution
               WHERE resolution."TelemetryCommandDispatchId" = dispatch."Id"
           ))

    UNION ALL

    SELECT
        'local_command'::text AS source,
        command."Id"::text AS command_id,
        command."Status" AS status,
        command."Error" AS result_code,
        NULL::text AS owner_node_id
    FROM "TelemetryCommands" command
    WHERE (command."Status" = 'accepted' AND command."Error" = 'command_intent_persisted')
       OR (command."Status" = 'failed'
           AND command."Error" IN ('ack_timeout', 'command_outcome_unknown')
           AND NOT EXISTS (
               SELECT 1 FROM "TelemetryCommandResolutions" resolution
               WHERE resolution."TelemetryCommandId" = command."Id"
           ))
) unresolved
ORDER BY source, command_id;

ROLLBACK;
