-- Execute somente em uma cópia recente e restrita do PostgreSQL antes da
-- migration 20260927163720_BootstrapPlatformAdmin. Não salve os resultados em
-- chamados ou logs de acesso amplo.

-- Esperado: total_encontrado = 1 e contas_ativas = 1.
SELECT
    COUNT(*) AS total_encontrado,
    COUNT(*) FILTER (WHERE "IsActive") AS contas_ativas,
    COUNT(*) FILTER (WHERE "Role" = 'PlatformAdmin') AS ja_sao_platform_admin
FROM "Users"
WHERE "NormalizedEmail" = 'dev.ivan@gmail.com';

-- Confirme com o responsável que o registro retornado pertence à conta inicial
-- autorizada. Pare a release se o resultado não for exatamente uma conta ativa.
SELECT "Id", "Role", "IsActive", "CompanyId"
FROM "Users"
WHERE "NormalizedEmail" = 'dev.ivan@gmail.com';
