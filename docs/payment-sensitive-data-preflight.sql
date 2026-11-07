-- Execute read-only against a restricted production copy before scheduling cleanup.
-- This reports counts and approximate bytes only; it never returns stored payloads.
SELECT
    COUNT(*) FILTER (WHERE "RawResponse" IS NOT NULL) AS transactions_with_raw_response,
    COALESCE(SUM(octet_length("RawResponse")) FILTER (WHERE "RawResponse" IS NOT NULL), 0) AS raw_response_bytes,
    MIN("CreatedAt") FILTER (WHERE "RawResponse" IS NOT NULL) AS oldest_transaction_with_raw_response,
    MAX("CreatedAt") FILTER (WHERE "RawResponse" IS NOT NULL) AS newest_transaction_with_raw_response
FROM "PaymentTransactions";

SELECT
    COUNT(*) AS legacy_sensitive_logs,
    COALESCE(SUM(octet_length("Message")), 0) AS message_bytes
FROM "TransactionTelemetryLogs"
WHERE "Message" LIKE 'Erro retornado pelo Mercado Pago:%'
   OR "Message" LIKE 'Falha na integração real do Mercado Pago:%'
   OR "Message" LIKE 'Falha ao solicitar estorno na API do Mercado Pago:%'
   OR "Message" LIKE 'Erro interno ao tentar estorno no Mercado Pago:%'
   OR "Message" LIKE 'Falha ao validar o dono do token OAuth em /users/me:%';

SELECT
    COUNT(*) AS legacy_sensitive_token_statuses,
    COALESCE(SUM(octet_length("LastTokenValidationStatus")), 0) AS status_bytes
FROM "MercadoPagoIntegrations"
WHERE "LastTokenValidationStatus" LIKE 'Falha ao renovar token OAuth:%'
   OR "LastTokenValidationStatus" LIKE 'Falha ao validar token OAuth antes do Pix:%';
