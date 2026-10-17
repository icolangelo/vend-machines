-- Execute somente em uma cópia recente e restrita do PostgreSQL.
-- EXPLAIN ANALYZE executa a seleção, mas não insere nem altera SaleRecords.
-- A consulta reproduz o conjunto de sessões usado em BackfillDeliveredSales.

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
      AND session."ItemNumber" IS NOT NULL
      AND tx."Status" = 'Approved'
    ORDER BY session."Id", telemetry."CreatedAt" ASC
)
SELECT count(*) AS eligible_legacy_sales
FROM legacy_sales;

ROLLBACK;
