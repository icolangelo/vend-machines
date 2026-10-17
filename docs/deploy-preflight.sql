-- Consulta somente leitura. Execute com acesso operacional autorizado e depois
-- de confirmar que o schema atual inclui MachineSessions. Não retorna dados
-- de cliente, máquina ou transação.
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

ROLLBACK;
