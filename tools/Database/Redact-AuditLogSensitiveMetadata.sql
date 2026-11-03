\set ON_ERROR_STOP on
\if :{?apply}
\else
\set apply false
\endif

-- Default is a count-only preflight. Set -v apply=true only after a verified backup.
SET lock_timeout = '1s';
SET statement_timeout = '15s';

CREATE OR REPLACE FUNCTION pg_temp.try_parse_audit_metadata(p_value text)
RETURNS jsonb
LANGUAGE plpgsql
IMMUTABLE
AS $function$
BEGIN
    RETURN p_value::jsonb;
EXCEPTION WHEN OTHERS THEN
    RETURN NULL;
END;
$function$;

CREATE OR REPLACE FUNCTION pg_temp.redact_audit_jsonb(p_value jsonb)
RETURNS jsonb
LANGUAGE plpgsql
IMMUTABLE
STRICT
AS $function$
DECLARE
    v_key text;
    v_value jsonb;
    v_result jsonb;
    v_normalized_key text;
BEGIN
    IF jsonb_typeof(p_value) = 'object' THEN
        v_result := '{}'::jsonb;
        FOR v_key, v_value IN SELECT key, value FROM jsonb_each(p_value)
        LOOP
            v_normalized_key := lower(regexp_replace(v_key, '[^[:alnum:]]', '', 'g'));
            IF v_normalized_key ~ '(password|secret|token|credential|authorization|cookie|header|payload|body|apikey|privatekey|signature|email|phone|cpf|cnpj|document)' THEN
                v_result := v_result || jsonb_build_object(v_key, '[REDACTED]');
            ELSE
                v_result := v_result || jsonb_build_object(v_key, pg_temp.redact_audit_jsonb(v_value));
            END IF;
        END LOOP;
        RETURN v_result;
    ELSIF jsonb_typeof(p_value) = 'array' THEN
        SELECT COALESCE(
            jsonb_agg(pg_temp.redact_audit_jsonb(item.value) ORDER BY item.ordinality),
            '[]'::jsonb)
        INTO v_result
        FROM jsonb_array_elements(p_value) WITH ORDINALITY AS item(value, ordinality);
        RETURN v_result;
    END IF;

    RETURN p_value;
END;
$function$;

CREATE OR REPLACE FUNCTION pg_temp.sanitize_audit_metadata(p_value text)
RETURNS text
LANGUAGE plpgsql
IMMUTABLE
AS $function$
DECLARE
    v_json jsonb;
    v_redacted jsonb;
BEGIN
    v_json := pg_temp.try_parse_audit_metadata(p_value);
    IF v_json IS NULL OR jsonb_typeof(v_json) NOT IN ('object', 'array') THEN
        RETURN NULL;
    END IF;

    v_redacted := pg_temp.redact_audit_jsonb(v_json);
    IF v_redacted = v_json THEN
        RETURN p_value;
    END IF;

    RETURN v_redacted::text;
END;
$function$;

\echo 'Preflight: only counts are returned; metadata values and row identifiers are never displayed.'
WITH parsed AS (
    SELECT
        "MetadataJson",
        pg_temp.try_parse_audit_metadata("MetadataJson") AS parsed_json
    FROM "AuditLogs"
    WHERE "MetadataJson" IS NOT NULL
)
SELECT
    count(*) AS rows_with_metadata,
    count(*) FILTER (WHERE parsed_json IS NULL) AS invalid_json_rows,
    count(*) FILTER (
        WHERE parsed_json IS NOT NULL
          AND jsonb_typeof(parsed_json) NOT IN ('object', 'array')) AS scalar_json_rows,
    count(*) FILTER (
        WHERE parsed_json IS NOT NULL
          AND jsonb_typeof(parsed_json) IN ('object', 'array')
          AND pg_temp.redact_audit_jsonb(parsed_json) IS DISTINCT FROM parsed_json) AS rows_with_sensitive_keys,
    count(*) FILTER (
        WHERE "MetadataJson" IS DISTINCT FROM pg_temp.sanitize_audit_metadata("MetadataJson")) AS rows_to_change
FROM parsed;

\if :apply
\echo 'APPLY=true: sensitive audit metadata will be redacted in committed batches of at most 100 rows.'
RESET statement_timeout;

CREATE OR REPLACE PROCEDURE pg_temp.redact_audit_metadata_batches(p_batch_size integer)
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
            SELECT
                "Id",
                "MetadataJson"
            FROM "AuditLogs"
            WHERE "MetadataJson" IS NOT NULL
              AND (v_after_id IS NULL OR "Id" > v_after_id)
            ORDER BY "Id"
            LIMIT p_batch_size
            FOR UPDATE SKIP LOCKED
        ), prepared AS MATERIALIZED (
            SELECT
                "Id",
                "MetadataJson",
                pg_temp.sanitize_audit_metadata("MetadataJson") AS sanitized_metadata
            FROM scanned
        ), redacted AS MATERIALIZED (
            SELECT "Id", sanitized_metadata
            FROM prepared
            WHERE "MetadataJson" IS DISTINCT FROM sanitized_metadata
        ), updated AS (
            UPDATE "AuditLogs" AS audit_log
            SET "MetadataJson" = redacted.sanitized_metadata
            FROM redacted
            WHERE audit_log."Id" = redacted."Id"
            RETURNING audit_log."Id"
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

    RAISE NOTICE 'Audit log rows redacted: %', v_total_updated;
END;
$procedure$;

CALL pg_temp.redact_audit_metadata_batches(100);
SET statement_timeout = '15s';

\else
\echo 'APPLY=false: no AuditLogs rows were changed. Re-run only after backup and approval to redact at rest.'
\endif

\echo 'Postflight: rows_to_change must be zero before closing the cleanup.'
WITH parsed AS (
    SELECT
        "MetadataJson",
        pg_temp.try_parse_audit_metadata("MetadataJson") AS parsed_json
    FROM "AuditLogs"
    WHERE "MetadataJson" IS NOT NULL
)
SELECT
    count(*) FILTER (
        WHERE "MetadataJson" IS DISTINCT FROM pg_temp.sanitize_audit_metadata("MetadataJson")) AS rows_to_change
FROM parsed;

RESET statement_timeout;
RESET lock_timeout;
