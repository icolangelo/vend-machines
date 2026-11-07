-- Execute only against a recent, restricted copy of the production database.
-- This read-only report mirrors the predicate in RemoveStaticIntegrations and
-- returns counts only; it does not expose company IDs or integration credentials.
BEGIN TRANSACTION READ ONLY;
SET LOCAL lock_timeout = '1s';
SET LOCAL statement_timeout = '5s';

SELECT
    count(*)::bigint AS integrations_to_delete,
    count(*) FILTER (WHERE "IsActive")::bigint AS active_integrations_to_delete,
    count(*) FILTER (WHERE NOT "IsActive")::bigint AS inactive_integrations_to_delete
FROM "MercadoPagoIntegrations"
WHERE "ClientId" IS NULL OR "ClientId" = '';

ROLLBACK;
