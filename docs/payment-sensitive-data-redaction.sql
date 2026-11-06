-- Run one batch per invocation; each invocation commits independently under psql
-- autocommit. Repeat until both statements report UPDATE 0. Safe to rerun.
SET lock_timeout = '2s';
SET statement_timeout = '30s';

WITH candidates AS MATERIALIZED (
    SELECT ctid
    FROM "PaymentTransactions"
    WHERE "RawResponse" IS NOT NULL
    LIMIT 500
    FOR UPDATE SKIP LOCKED
)
UPDATE "PaymentTransactions" AS payment
SET "RawResponse" = NULL
FROM candidates
WHERE payment.ctid = candidates.ctid;

WITH candidates AS MATERIALIZED (
    SELECT ctid
    FROM "TransactionTelemetryLogs"
    WHERE "Message" LIKE 'Erro retornado pelo Mercado Pago:%'
       OR "Message" LIKE 'Falha na integração real do Mercado Pago:%'
       OR "Message" LIKE 'Falha ao solicitar estorno na API do Mercado Pago:%'
       OR "Message" LIKE 'Erro interno ao tentar estorno no Mercado Pago:%'
       OR "Message" LIKE 'Falha ao validar o dono do token OAuth em /users/me:%'
    LIMIT 500
    FOR UPDATE SKIP LOCKED
)
UPDATE "TransactionTelemetryLogs" AS log
SET "Message" = 'Falha na integração de pagamento. Detalhes sensíveis foram removidos.'
FROM candidates
WHERE log.ctid = candidates.ctid;

WITH candidates AS MATERIALIZED (
    SELECT ctid
    FROM "MercadoPagoIntegrations"
    WHERE "LastTokenValidationStatus" LIKE 'Falha ao renovar token OAuth:%'
       OR "LastTokenValidationStatus" LIKE 'Falha ao validar token OAuth antes do Pix:%'
    LIMIT 500
    FOR UPDATE SKIP LOCKED
)
UPDATE "MercadoPagoIntegrations" AS integration
SET "LastTokenValidationStatus" = 'Falha na validação do token OAuth. Detalhes sensíveis foram removidos.'
FROM candidates
WHERE integration.ctid = candidates.ctid;
