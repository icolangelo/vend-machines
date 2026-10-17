using Microsoft.EntityFrameworkCore.Migrations;
using VendingMachines.Api.Models;

#nullable disable

namespace VendingMachines.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyActivationStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActivationStatus",
                table: "Companies",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: CompanyActivationStatuses.Active);

            migrationBuilder.AlterColumn<string>(
                name: "ActivationStatus",
                table: "Companies",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: CompanyActivationStatuses.Pending,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16,
                oldDefaultValue: CompanyActivationStatuses.Active);

            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql("""
                    CREATE OR REPLACE FUNCTION "DisableUserForPendingCompany"()
                    RETURNS trigger AS $$
                    BEGIN
                        IF NEW."CompanyId" IS NOT NULL AND EXISTS (
                            SELECT 1 FROM "Companies"
                            WHERE "Id" = NEW."CompanyId" AND "ActivationStatus" <> 'Active'
                        ) THEN
                            NEW."IsActive" := FALSE;
                        END IF;
                        RETURN NEW;
                    END;
                    $$ LANGUAGE plpgsql;

                    CREATE TRIGGER "TR_Users_DisableUserForPendingCompany"
                    BEFORE INSERT ON "Users"
                    FOR EACH ROW EXECUTE FUNCTION "DisableUserForPendingCompany"();
                    """);
            }
            else if (migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
            {
                migrationBuilder.Sql("""
                    CREATE TRIGGER IF NOT EXISTS "TR_Users_DisableUserForPendingCompany"
                    AFTER INSERT ON "Users"
                    FOR EACH ROW
                    WHEN NEW."CompanyId" IS NOT NULL AND EXISTS (
                        SELECT 1 FROM "Companies"
                        WHERE "Id" = NEW."CompanyId" AND "ActivationStatus" <> 'Active'
                    )
                    BEGIN
                        UPDATE "Users" SET "IsActive" = 0 WHERE "Id" = NEW."Id";
                    END;
                    """);
            }
            else
            {
                throw new NotSupportedException($"Provider de banco não suportado para ativação de empresas: {migrationBuilder.ActiveProvider}");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql("""
                    DROP TRIGGER IF EXISTS "TR_Users_DisableUserForPendingCompany" ON "Users";
                    DROP FUNCTION IF EXISTS "DisableUserForPendingCompany"();
                    """);
            }
            else if (migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
            {
                migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"TR_Users_DisableUserForPendingCompany\";");
            }

            migrationBuilder.DropColumn(
                name: "ActivationStatus",
                table: "Companies");
        }
    }
}
