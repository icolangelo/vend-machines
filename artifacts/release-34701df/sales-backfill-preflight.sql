-- Execute somente em uma cópia recente e restrita do PostgreSQL.
-- EXPLAIN ANALYZE executa a seleção, mas não insere nem altera SaleRecords.
-- A consulta reproduz o conjunto de sessões usado em 20261026035921_BackfillLegacyDeliveredSales.

BEGIN READ ONLY;
SET LOCAL statement_timeout = '10min';

SELECT relation_name,
       pg_size_pretty(pg_total_relation_size(to_regclass(format('%I.%I', current_schema(), relation_name)))) AS total_size,
       GREATEST(c.reltuples, 0)::bigint AS estimated_rows
FROM (VALUES ('TelemetryEvents'), ('MachineSessions'), ('PaymentTransactions'), ('SaleRecords')) AS relations(relation_name)
LEFT JOIN pg_class c
       ON c.oid = to_regclass(format('%I.%I', current_schema(), relation_name))
ORDER BY relation_name;

EXPLAIN (ANALYZE, BUFFERS, SUMMARY)
WITH legacy_sales AS (
    SELECT DISTINCT ON (session."Id")
        session."CompanyId",
        session."MachineId",
        session."Id" AS "SessionId",
        tx."Id" AS "TransactionId",
        session."ItemNumber",
        tx."Amount" AS "GrossAmount",
        tx."ApplicationFee",
        tx."Amount" - tx."ApplicationFee" AS "NetAmount",
        telemetry."CreatedAt" AS "SoldAt"
    FROM "TelemetryEvents" AS telemetry
    INNER JOIN "MachineSessions" AS session ON session."Id" = telemetry."SessionId"
    INNER JOIN "PaymentTransactions" AS tx ON tx."Id" = telemetry."TransactionId"
    WHERE telemetry."EventType" = 'vend.success'
      AND telemetry."SessionId" IS NOT NULL
      AND telemetry."TransactionId" IS NOT NULL
      AND session."TransactionId" = tx."Id"
      AND session."CompanyId" = tx."CompanyId"
      AND session."MachineId" = tx."MachineId"
      AND telemetry."CompanyId" = session."CompanyId"
      AND telemetry."MachineId" = session."MachineId"
      AND session."ItemNumber" IS NOT NULL
      AND tx."Status" = 'Approved'
    ORDER BY session."Id", telemetry."CreatedAt" ASC
)
SELECT count(*) AS eligible_legacy_sales,
       count(*) FILTER (WHERE EXISTS (
           SELECT 1
           FROM "SaleRecords" AS existing
           WHERE existing."SessionId" = legacy."SessionId"
             AND existing."TransactionId" = legacy."TransactionId"
             AND existing."CompanyId" = legacy."CompanyId"
             AND existing."MachineId" = legacy."MachineId"
             AND existing."ItemNumber" = legacy."ItemNumber"
             AND existing."Quantity" = 1
             AND existing."GrossAmount" = legacy."GrossAmount"
             AND existing."ApplicationFee" = legacy."ApplicationFee"
             AND existing."NetAmount" = legacy."NetAmount"
       )) AS already_materialized_sales,
       count(*) FILTER (WHERE
           (EXISTS (
               SELECT 1
               FROM "SaleRecords" AS existing
               WHERE existing."SessionId" = legacy."SessionId"
                  OR existing."TransactionId" = legacy."TransactionId"
                  OR existing."Id" = md5(legacy."SessionId"::text || ':' || legacy."TransactionId"::text)::uuid
           ))
           AND NOT EXISTS (
               SELECT 1
               FROM "SaleRecords" AS existing
               WHERE existing."SessionId" = legacy."SessionId"
                 AND existing."TransactionId" = legacy."TransactionId"
                 AND existing."CompanyId" = legacy."CompanyId"
                 AND existing."MachineId" = legacy."MachineId"
                 AND existing."ItemNumber" = legacy."ItemNumber"
                 AND existing."Quantity" = 1
                 AND existing."GrossAmount" = legacy."GrossAmount"
                 AND existing."ApplicationFee" = legacy."ApplicationFee"
                 AND existing."NetAmount" = legacy."NetAmount"
           )
       ) AS conflicting_existing_sales,
       count(*) FILTER (WHERE NOT EXISTS (
           SELECT 1
           FROM "SaleRecords" AS existing
           WHERE existing."SessionId" = legacy."SessionId"
              OR existing."TransactionId" = legacy."TransactionId"
              OR existing."Id" = md5(legacy."SessionId"::text || ':' || legacy."TransactionId"::text)::uuid
       )) AS anticipated_new_sales
FROM legacy_sales AS legacy;

SELECT count(*) FILTER (
           WHERE telemetry."SessionId" IS NULL
       ) AS events_without_session_id,
       count(*) FILTER (
           WHERE telemetry."SessionId" IS NOT NULL AND session."Id" IS NULL
       ) AS events_with_unknown_session,
       count(*) FILTER (
           WHERE telemetry."TransactionId" IS NULL
       ) AS events_without_transaction_id,
       count(*) FILTER (
           WHERE telemetry."TransactionId" IS NOT NULL AND tx."Id" IS NULL
       ) AS events_with_unknown_transaction,
       count(*) FILTER (
           WHERE session."Id" IS NOT NULL AND session."ItemNumber" IS NULL
       ) AS events_without_session_item,
       count(*) FILTER (
           WHERE tx."Id" IS NOT NULL AND tx."Status" <> 'Approved'
       ) AS events_with_non_approved_transaction,
       count(*) FILTER (
           WHERE session."Id" IS NOT NULL
             AND tx."Id" IS NOT NULL
             AND session."TransactionId" = tx."Id"
             AND session."CompanyId" = tx."CompanyId"
             AND session."MachineId" = tx."MachineId"
             AND telemetry."CompanyId" = session."CompanyId"
             AND telemetry."MachineId" = session."MachineId"
             AND session."ItemNumber" IS NOT NULL
             AND tx."Status" = 'Approved'
       ) AS eligible_vend_success_events,
       count(DISTINCT session."Id") FILTER (
           WHERE session."ItemNumber" IS NOT NULL
             AND tx."Status" = 'Approved'
             AND session."TransactionId" IS DISTINCT FROM tx."Id"
       ) AS transaction_scope_mismatches,
       count(DISTINCT session."Id") FILTER (
           WHERE session."ItemNumber" IS NOT NULL
             AND tx."Status" = 'Approved'
             AND session."CompanyId" IS DISTINCT FROM tx."CompanyId"
       ) AS company_scope_mismatches,
       count(DISTINCT session."Id") FILTER (
           WHERE session."ItemNumber" IS NOT NULL
             AND tx."Status" = 'Approved'
             AND session."MachineId" IS DISTINCT FROM tx."MachineId"
       ) AS machine_scope_mismatches,
       count(DISTINCT session."Id") FILTER (
           WHERE session."Id" IS NOT NULL
             AND session."ItemNumber" IS NOT NULL
             AND tx."Status" = 'Approved'
             AND telemetry."CompanyId" IS DISTINCT FROM session."CompanyId"
       ) AS telemetry_company_scope_mismatches,
       count(DISTINCT session."Id") FILTER (
           WHERE session."Id" IS NOT NULL
             AND session."ItemNumber" IS NOT NULL
             AND tx."Status" = 'Approved'
             AND telemetry."MachineId" IS DISTINCT FROM session."MachineId"
       ) AS telemetry_machine_scope_mismatches
FROM "TelemetryEvents" AS telemetry
LEFT JOIN "MachineSessions" AS session ON session."Id" = telemetry."SessionId"
LEFT JOIN "PaymentTransactions" AS tx ON tx."Id" = telemetry."TransactionId"
WHERE telemetry."EventType" = 'vend.success';

ROLLBACK;
