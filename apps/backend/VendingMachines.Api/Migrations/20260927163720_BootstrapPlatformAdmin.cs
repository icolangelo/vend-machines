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

                    WITH promoted AS (
                        UPDATE "Users"
                        SET "Role" = 'PlatformAdmin',
                            "CredentialVersion" = "CredentialVersion" + 1
                        WHERE "NormalizedEmail" = 'dev.ivan@gmail.com'
                          AND "IsActive" = TRUE
                          AND "Role" <> 'PlatformAdmin'
                        RETURNING "Id", "CompanyId"
                    )
                    INSERT INTO "AuditLogs"
                        ("Id", "CompanyId", "ActorUserId", "Action", "ResourceType", "ResourceId", "MetadataJson", "CreatedAt")
                    SELECT gen_random_uuid(), "CompanyId", NULL,
                           'auth.platform_admin_bootstrap_promoted', 'User', "Id"::text,
                           '{"source":"migration"}', CURRENT_TIMESTAMP
                    FROM promoted;
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
                    SELECT "Id", "CompanyId"
                    FROM "Users"
                    WHERE "NormalizedEmail" = 'dev.ivan@gmail.com'
                      AND "IsActive" = 1
                      AND "Role" <> 'PlatformAdmin';

                    UPDATE "Users"
                    SET "Role" = 'PlatformAdmin',
                        "CredentialVersion" = "CredentialVersion" + 1
                    WHERE "Id" IN (SELECT "Id" FROM "__BootstrapPlatformAdmin");

                    INSERT INTO "AuditLogs"
                        ("Id", "CompanyId", "ActorUserId", "Action", "ResourceType", "ResourceId", "MetadataJson", "CreatedAt")
                    SELECT lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-' ||
                           lower(hex(randomblob(2))) || '-' || lower(hex(randomblob(2))) || '-' ||
                           lower(hex(randomblob(6))), "CompanyId", NULL,
                           'auth.platform_admin_bootstrap_promoted', 'User', "Id",
                           '{"source":"migration"}', CURRENT_TIMESTAMP
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
            // Intencionalmente sem reversão automática: uma concessão de papel
            // pode ter sido usada ou alterada após a migration. Revise manualmente.
        }
    }
}
