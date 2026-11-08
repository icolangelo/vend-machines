-- Encerramento administrativo das duas sessões conferidas pelo responsável.
-- Pré-requisitos confirmados: backup guardado e equipamentos sem operação pendente.
-- Execute inteiro; não altera comandos nem afirma recebimento de FECHAR_SESSAO.
BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '15s';

DO $maintenance$
DECLARE
    expected record;
    session_row record;
BEGIN
    FOR expected IN
        SELECT * FROM (VALUES
            ('6f82ebd3-4578-4914-af28-4d6f5fad392d'::uuid, 'VM-B174FD3C',
             '2026-09-30 06:46:28.394896+00'::timestamptz),
            ('a267c1f8-bd0a-4b69-af8b-314b332445fd'::uuid, 'VM-0B735A30',
             '2026-11-05 07:24:25.187238+00'::timestamptz)
        ) AS targets(session_id, machine_id, last_event_at)
        ORDER BY session_id
    LOOP
        SELECT * INTO session_row
        FROM "MachineSessions"
        WHERE "Id" = expected.session_id
        FOR UPDATE;

        IF NOT FOUND THEN
            RAISE EXCEPTION 'Sessão esperada não encontrada: %', expected.session_id;
        END IF;

        -- Reexecução não duplica o evento nem altera o horário de encerramento.
        IF session_row."MachineId" = expected.machine_id
           AND session_row."State" = 'ClosedWithoutSelection'
           AND session_row."ClosedAt" IS NOT NULL
           AND session_row."TransactionId" IS NULL
           AND session_row."CloseReason" = 'maintenance_physical_idle_confirmed' THEN
            CONTINUE;
        END IF;

        IF session_row."MachineId" IS DISTINCT FROM expected.machine_id
           OR session_row."State" IS DISTINCT FROM 'Opening'
           OR session_row."ClosedAt" IS NOT NULL
           OR session_row."TransactionId" IS NOT NULL
           OR session_row."LastEventAt" IS DISTINCT FROM expected.last_event_at THEN
            RAISE EXCEPTION 'A sessão % mudou desde a conferência; nenhuma alteração será confirmada.', expected.session_id;
        END IF;

        IF NOT EXISTS (
            SELECT 1 FROM "TelemetryCommands"
            WHERE "SessionId" = expected.session_id
              AND "MachineId" = expected.machine_id
              AND "Command" = 'ABRIR_SESSAO'
              AND "Status" = 'acknowledged'
              AND "AckAt" IS NOT NULL
        ) OR EXISTS (
            SELECT 1 FROM "TelemetryCommands"
            WHERE "SessionId" = expected.session_id
              AND ("Status" IS DISTINCT FROM 'acknowledged'
                   OR "TransactionId" IS NOT NULL
                   OR "MachineId" IS DISTINCT FROM expected.machine_id)
        ) THEN
            RAISE EXCEPTION 'Comandos da sessão % exigem nova conferência; nenhuma alteração será confirmada.', expected.session_id;
        END IF;

        INSERT INTO "TelemetryEvents"
            ("Id", "CompanyId", "MachineId", "SessionId", "TransactionId",
             "UserId", "EventType", "Detail", "DataJson", "CreatedAt")
        VALUES (
            gen_random_uuid(), session_row."CompanyId", session_row."MachineId",
            session_row."Id", NULL, NULL, 'session.maintenance_closed',
            'Encerramento administrativo após confirmação do responsável: equipamento sem operação pendente.',
            jsonb_build_object(
                'previousState', session_row."State",
                'previousLastEventAt', session_row."LastEventAt",
                'physicalIdleConfirmedByOperator', TRUE,
                'deviceCloseAcknowledgementObserved', FALSE
            )::text,
            CURRENT_TIMESTAMP
        );

        UPDATE "MachineSessions"
        SET "State" = 'ClosedWithoutSelection',
            "ClosedAt" = CURRENT_TIMESTAMP,
            "LastEventAt" = CURRENT_TIMESTAMP,
            "CloseReason" = 'maintenance_physical_idle_confirmed'
        WHERE "Id" = expected.session_id;
    END LOOP;
END;
$maintenance$;

COMMIT;

SELECT "Id", "MachineId", "State", "ClosedAt", "CloseReason"
FROM "MachineSessions"
WHERE "Id" IN (
    '6f82ebd3-4578-4914-af28-4d6f5fad392d'::uuid,
    'a267c1f8-bd0a-4b69-af8b-314b332445fd'::uuid
)
ORDER BY "Id";
