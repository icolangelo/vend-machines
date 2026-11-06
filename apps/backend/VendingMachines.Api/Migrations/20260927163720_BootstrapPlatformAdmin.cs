using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VendingMachines.Api.Migrations
{
    /// <inheritdoc />
    public partial class BootstrapPlatformAdmin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql("""
                    DO $bootstrap$
                    DECLARE active_accounts integer;
                    BEGIN
                        SELECT COUNT(*) INTO active_accounts
                        FROM "Users"
                        WHERE "NormalizedEmail" = 'dev.ivan@gmail.com'
                          AND "IsActive" = TRUE;
                        IF active_accounts <> 1 THEN
                            RAISE EXCEPTION 'PlatformAdmin bootstrap requires exactly one active reserved account; found %', active_accounts;
                        END IF;
                    END;
                    $bootstrap$;

                    WITH bootstrap_account AS MATERIALIZED (
                        SELECT "Id", "CompanyId", "Role" AS "PreviousRole"
                        FROM "Users"
                        WHERE "NormalizedEmail" = 'dev.ivan@gmail.com'
                          AND "IsActive" = TRUE
                    ), promoted AS (
                        UPDATE "Users" AS user_account
                        SET "Role" = 'PlatformAdmin',
                            "CredentialVersion" = user_account."CredentialVersion" + 1
                        FROM bootstrap_account
                        WHERE user_account."Id" = bootstrap_account."Id"
                          AND user_account."Role" <> 'PlatformAdmin'
                        RETURNING user_account."Id"
                    )
                    INSERT INTO "AuditLogs"
                        ("Id", "CompanyId", "ActorUserId", "Action", "ResourceType", "ResourceId", "MetadataJson", "CreatedAt")
                    SELECT gen_random_uuid(), "CompanyId", NULL,
                           'auth.platform_admin_bootstrap_completed', 'User', "Id"::text,
                           jsonb_build_object(
                               'source', 'migration',
                               'roleChanged', ("PreviousRole" <> 'PlatformAdmin')
                           )::text,
                           CURRENT_TIMESTAMP
                    FROM bootstrap_account;
                    """);
            }
            else if (migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
            {
                migrationBuilder.Sql("""
                    CREATE TEMP TABLE "__BootstrapPlatformAdminGuard" (
                        "Valid" INTEGER NOT NULL CHECK ("Valid" = 1)
                    );
                    INSERT INTO "__BootstrapPlatformAdminGuard" ("Valid")
                    SELECT CASE WHEN COUNT(*) = 1 THEN 1 ELSE 0 END
                    FROM "Users"
                    WHERE "NormalizedEmail" = 'dev.ivan@gmail.com'
                      AND "IsActive" = 1;
                    DROP TABLE "__BootstrapPlatformAdminGuard";

                    CREATE TEMP TABLE "__BootstrapPlatformAdmin" AS
                    SELECT "Id", "CompanyId", "Role" AS "PreviousRole"
                    FROM "Users"
                    WHERE "NormalizedEmail" = 'dev.ivan@gmail.com'
                      AND "IsActive" = 1;

                    UPDATE "Users"
                    SET "Role" = 'PlatformAdmin',
                        "CredentialVersion" = "CredentialVersion" + 1
                    WHERE "Id" IN (
                        SELECT "Id" FROM "__BootstrapPlatformAdmin" WHERE "PreviousRole" <> 'PlatformAdmin'
                    );

                    INSERT INTO "AuditLogs"
                        ("Id", "CompanyId", "ActorUserId", "Action", "ResourceType", "ResourceId", "MetadataJson", "CreatedAt")
                    SELECT lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-' ||
                           lower(hex(randomblob(2))) || '-' || lower(hex(randomblob(2))) || '-' ||
                           lower(hex(randomblob(6))), "CompanyId", NULL,
                           'auth.platform_admin_bootstrap_completed', 'User', "Id",
                           CASE WHEN "PreviousRole" = 'PlatformAdmin'
                                THEN '{"source":"migration","roleChanged":false}'
                                ELSE '{"source":"migration","roleChanged":true}'
                           END,
                           CURRENT_TIMESTAMP
                    FROM "__BootstrapPlatformAdmin";

                    DROP TABLE "__BootstrapPlatformAdmin";
                    """);
            }
            else
            {
                throw new NotSupportedException($"Provider de banco não suportado para bootstrap de PlatformAdmin: {migrationBuilder.ActiveProvider}");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new System.InvalidOperationException(
                "Esta migration promove a conta reservada, atualiza a versão da credencial quando necessário e grava auditoria. O Down agora falha fechado para preservar o papel e a evidência; correções devem seguir procedimento manual auditado.");
        }
    }
}
