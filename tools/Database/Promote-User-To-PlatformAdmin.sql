-- Promove uma conta ativa existente a PlatformAdmin no PostgreSQL.
-- Execute com psql passando target_email; este arquivo não deve ser aplicado
-- automaticamente na inicialização da API.
--
-- Exemplo (PowerShell):
-- psql $env:DATABASE_URL -v ON_ERROR_STOP=1 -v target_email='voce@empresa.com' -f tools/Database/Promote-User-To-PlatformAdmin.sql

\set ON_ERROR_STOP on

\if :{?target_email}
\else
  \echo 'ERRO: informe -v target_email=voce@empresa.com'
  \quit 3
\endif

BEGIN;

SELECT set_config(
    'vend_machines.platform_admin_target_email',
    upper(trim(:'target_email')),
    true
);

-- Falha fechada se o e-mail não identificar exatamente uma conta ativa.
CREATE TEMP TABLE "__PlatformAdminPromotionGuard" (
    "Valid" boolean NOT NULL CHECK ("Valid")
) ON COMMIT DROP;

INSERT INTO "__PlatformAdminPromotionGuard" ("Valid")
SELECT COUNT(*) = 1
FROM "Users"
WHERE "NormalizedEmail" = current_setting('vend_machines.platform_admin_target_email')
  AND "IsActive" = TRUE;

WITH candidate AS MATERIALIZED (
    SELECT "Id", "CompanyId", "Role" AS "PreviousRole"
    FROM "Users"
    WHERE "NormalizedEmail" = current_setting('vend_machines.platform_admin_target_email')
      AND "IsActive" = TRUE
    FOR UPDATE
), promoted AS (
    UPDATE "Users" AS user_account
    SET "Role" = 'PlatformAdmin',
        "CredentialVersion" = user_account."CredentialVersion" + 1
    FROM candidate
    WHERE user_account."Id" = candidate."Id"
      AND candidate."PreviousRole" <> 'PlatformAdmin'
    RETURNING user_account."Id", user_account."CompanyId"
), revoked AS (
    UPDATE "RefreshSessions" AS session
    SET "RevokedAt" = CURRENT_TIMESTAMP
    FROM promoted
    WHERE session."UserId" = promoted."Id"
      AND session."RevokedAt" IS NULL
    RETURNING session."Id"
), audit AS (
    INSERT INTO "AuditLogs"
        ("Id", "CompanyId", "ActorUserId", "Action", "ResourceType", "ResourceId", "MetadataJson", "CreatedAt")
    SELECT gen_random_uuid(), candidate."CompanyId", NULL,
           'auth.platform_admin_promoted_manual', 'User', candidate."Id"::text,
           jsonb_build_object(
               'source', 'manual_operator_script',
               'previousRole', candidate."PreviousRole",
               'newRole', 'PlatformAdmin',
               'credentialVersionIncremented', true
           )::text,
           CURRENT_TIMESTAMP
    FROM candidate
    JOIN promoted ON promoted."Id" = candidate."Id"
    RETURNING "Id"
)
SELECT candidate."Id" AS "UserId",
       candidate."PreviousRole",
       (promoted."Id" IS NOT NULL) AS "Changed",
       (SELECT COUNT(*) FROM revoked) AS "RevokedRefreshSessions",
       (SELECT COUNT(*) FROM audit) AS "AuditEntriesCreated"
FROM candidate
LEFT JOIN promoted ON promoted."Id" = candidate."Id";

COMMIT;
