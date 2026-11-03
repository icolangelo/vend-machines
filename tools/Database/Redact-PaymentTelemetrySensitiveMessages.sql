\set ON_ERROR_STOP on
\if :{?apply}
\else
\set apply false
\endif

-- Default is a count-only preflight. Set -v apply=true only after a verified backup.
SET lock_timeout = '1s';
SET statement_timeout = '15s';

CREATE OR REPLACE FUNCTION pg_temp.sanitize_payment_log_message(p_value text)
RETURNS text
LANGUAGE plpgsql
IMMUTABLE
AS $function$
DECLARE
    v_result text;
BEGIN
    IF p_value IS NULL THEN
        RETURN NULL;
    END IF;

    IF p_value ~* '^(Erro retornado pelo Mercado Pago:|Falha na integração real do Mercado Pago:|Falha ao solicitar estorno na API do Mercado Pago:|Erro interno ao tentar estorno no Mercado Pago:|Falha ao validar o dono do token OAuth em /users/me:)' THEN
        RETURN 'Falha na integração de pagamento. Detalhes sensíveis foram omitidos.';
    END IF;

    IF p_value ~* '^(Usando token OAuth salvo na integração|Token OAuth validado no Mercado Pago\.|Token OAuth divergente\.|QR Code Pix gerado com sucesso pelo Mercado Pago\.|Estorno processado com sucesso na API do Mercado Pago\.)' THEN
        RETURN 'Detalhes internos da integração de pagamento foram omitidos.';
    END IF;

    -- Respostas JSON usam aspas ao redor da chave e do valor; preserve a sintaxe
    -- JSON enquanto remove o conteúdo de campos sensíveis.
    v_result := regexp_replace(
        p_value,
        E'("(access[_-]?token|refresh[_-]?token|client[_-]?secret|webhook[_-]?secret|oauth[_-]?state|authorization[_-]?code|authorization|password|passwd|credential|api[_-]?key|private[_-]?key|secret|token|signature|cookie)"[[:space:]]*:[[:space:]]*)"[^"]*"',
        E'\\1"[REDACTED]"',
        'gi');
    v_result := regexp_replace(
        v_result,
        E'\\m(access[_-]?token|refresh[_-]?token|client[_-]?secret|webhook[_-]?secret|oauth[_-]?state|authorization[_-]?code|authorization|password|passwd|credential|api[_-]?key|private[_-]?key|secret|token|signature|cookie)([[:space:]]*[:=][[:space:]]*)(Bearer[[:space:]]+)?("[^"]*"|''[^'']*''|[^,[:space:];&]+)',
        E'\\1\\2[REDACTED]', 'gi');
    v_result := regexp_replace(v_result, E'\\mBearer[[:space:]]+[A-Za-z0-9._~+/-]+=*', 'Bearer [REDACTED]', 'gi');
    v_result := regexp_replace(v_result, E'\\mAPP_USR-[A-Za-z0-9_-]{12,}\\M', '[REDACTED]', 'gi');
    v_result := regexp_replace(v_result, E'\\meyJ[A-Za-z0-9_-]*\\.[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+\\M', '[REDACTED]', 'g');
    v_result := regexp_replace(v_result, E'\\m[A-Z0-9._%+-]+@[A-Z0-9.-]+\\.[A-Z]{2,}\\M', '[REDACTED_EMAIL]', 'gi');
    RETURN v_result;
END;
$function$;

\echo 'Preflight: only counts are returned; messages and row identifiers are never displayed.'
SELECT
    count(*) AS rows_with_message,
    count(*) FILTER (WHERE "Message" IS DISTINCT FROM pg_temp.sanitize_payment_log_message("Message")) AS rows_to_change
FROM "TransactionTelemetryLogs"
WHERE "Message" IS NOT NULL;

\if :apply
\echo 'APPLY=true: sensitive payment log messages will be redacted in committed batches of at most 100 rows.'
RESET statement_timeout;

CREATE OR REPLACE PROCEDURE pg_temp.redact_payment_log_batches(p_batch_size integer)
LANGUAGE plpgsql
AS $procedure$
DECLARE
    v_updated integer;
    v_total_updated bigint := 0;
    v_after_id uuid := NULL;
    v_last_id uuid;
    v_scanned integer;
BEGIN
    IF p_batch_size < 1 OR p_batch_size > 100 THEN
        RAISE EXCEPTION 'batch size must be between 1 and 100';
    END IF;

    LOOP
        WITH scanned AS MATERIALIZED (
            SELECT "Id", "Message"
            FROM "TransactionTelemetryLogs"
            WHERE "Message" IS NOT NULL
              AND (v_after_id IS NULL OR "Id" > v_after_id)
            ORDER BY "Id"
            LIMIT p_batch_size
            FOR UPDATE SKIP LOCKED
        ), prepared AS MATERIALIZED (
            SELECT "Id", "Message",
                   pg_temp.sanitize_payment_log_message("Message") AS sanitized_message
            FROM scanned
        ), redacted AS MATERIALIZED (
            SELECT "Id", sanitized_message
            FROM prepared
            WHERE "Message" IS DISTINCT FROM sanitized_message
        ), updated AS (
            UPDATE "TransactionTelemetryLogs" AS log
            SET "Message" = redacted.sanitized_message
            FROM redacted
            WHERE log."Id" = redacted."Id"
            RETURNING log."Id"
        )
        SELECT
            (SELECT count(*)::integer FROM updated),
            (SELECT count(*)::integer FROM scanned),
            (SELECT "Id" FROM scanned ORDER BY "Id" DESC LIMIT 1)
        INTO v_updated, v_scanned, v_last_id;

        EXIT WHEN v_scanned = 0;
        v_total_updated := v_total_updated + v_updated;
        v_after_id := v_last_id;
        COMMIT;
    END LOOP;

    RAISE NOTICE 'Payment telemetry log rows redacted: %', v_total_updated;
END;
$procedure$;

CALL pg_temp.redact_payment_log_batches(100);
SET statement_timeout = '15s';
\else
\echo 'APPLY=false: no TransactionTelemetryLogs rows were changed. Re-run only after backup and approval.'
\endif

\echo 'Postflight: rows_to_change must be zero before closing the cleanup.'
SELECT count(*) AS rows_to_change
FROM "TransactionTelemetryLogs"
WHERE "Message" IS NOT NULL
  AND "Message" IS DISTINCT FROM pg_temp.sanitize_payment_log_message("Message");

RESET statement_timeout;
RESET lock_timeout;
