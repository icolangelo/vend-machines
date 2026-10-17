using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VendingMachines.Api.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeRegistrationIdentifiers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NormalizedCpf",
                table: "Users",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "NormalizedEmail",
                table: "Users",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "NormalizedCnpj",
                table: "Companies",
                type: "text",
                nullable: false,
                defaultValue: "");

            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql("""
                    UPDATE "Users"
                    SET "NormalizedEmail" = lower(trim(COALESCE("Email", ''))),
                        "NormalizedCpf" = regexp_replace(COALESCE("Cpf", ''), '[^0-9]', '', 'g');
                    UPDATE "Companies"
                    SET "NormalizedCnpj" = upper(regexp_replace(COALESCE("Cnpj", ''), '[^A-Za-z0-9]', '', 'g'));

                    CREATE OR REPLACE FUNCTION normalize_user_registration_identifiers()
                    RETURNS trigger
                    LANGUAGE plpgsql
                    AS $function$
                    BEGIN
                        NEW."NormalizedEmail" := lower(trim(COALESCE(NEW."Email", '')));
                        NEW."NormalizedCpf" := regexp_replace(COALESCE(NEW."Cpf", ''), '[^0-9]', '', 'g');
                        RETURN NEW;
                    END;
                    $function$;

                    CREATE TRIGGER "TR_Users_NormalizeRegistrationIdentifiers"
                    BEFORE INSERT OR UPDATE ON "Users"
                    FOR EACH ROW
                    EXECUTE FUNCTION normalize_user_registration_identifiers();

                    CREATE OR REPLACE FUNCTION normalize_company_registration_identifier()
                    RETURNS trigger
                    LANGUAGE plpgsql
                    AS $function$
                    BEGIN
                        NEW."NormalizedCnpj" := upper(regexp_replace(COALESCE(NEW."Cnpj", ''), '[^A-Za-z0-9]', '', 'g'));
                        RETURN NEW;
                    END;
                    $function$;

                    CREATE TRIGGER "TR_Companies_NormalizeRegistrationIdentifier"
                    BEFORE INSERT OR UPDATE ON "Companies"
                    FOR EACH ROW
                    EXECUTE FUNCTION normalize_company_registration_identifier();
                    """);
            }
            else if (migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
            {
                migrationBuilder.Sql("""
                    UPDATE "Users"
                    SET "NormalizedEmail" = lower(trim(COALESCE("Email", ''))),
                        "NormalizedCpf" = replace(replace(replace(replace(replace(replace(replace(
                            COALESCE("Cpf", ''), '.', ''), '-', ''), ' ', ''), char(9), ''), char(10), ''), char(13), ''), '/', '');
                    UPDATE "Companies"
                    SET "NormalizedCnpj" = upper(replace(replace(replace(replace(replace(replace(replace(
                        COALESCE("Cnpj", ''), '.', ''), '-', ''), ' ', ''), char(9), ''), char(10), ''), char(13), ''), '/', ''));

                    CREATE TRIGGER "TR_Users_NormalizeRegistrationIdentifiers_Insert"
                    AFTER INSERT ON "Users"
                    BEGIN
                        UPDATE "Users"
                        SET "NormalizedEmail" = lower(trim(COALESCE(NEW."Email", ''))),
                            "NormalizedCpf" = replace(replace(replace(replace(replace(replace(replace(
                                COALESCE(NEW."Cpf", ''), '.', ''), '-', ''), ' ', ''), char(9), ''), char(10), ''), char(13), ''), '/', '')
                        WHERE "Id" = NEW."Id";
                    END;

                    CREATE TRIGGER "TR_Users_NormalizeRegistrationIdentifiers_Update"
                    AFTER UPDATE OF "Email", "Cpf" ON "Users"
                    BEGIN
                        UPDATE "Users"
                        SET "NormalizedEmail" = lower(trim(COALESCE(NEW."Email", ''))),
                            "NormalizedCpf" = replace(replace(replace(replace(replace(replace(replace(
                                COALESCE(NEW."Cpf", ''), '.', ''), '-', ''), ' ', ''), char(9), ''), char(10), ''), char(13), ''), '/', '')
                        WHERE "Id" = NEW."Id";
                    END;

                    CREATE TRIGGER "TR_Companies_NormalizeRegistrationIdentifier_Insert"
                    AFTER INSERT ON "Companies"
                    BEGIN
                        UPDATE "Companies"
                        SET "NormalizedCnpj" = upper(replace(replace(replace(replace(replace(replace(replace(
                            COALESCE(NEW."Cnpj", ''), '.', ''), '-', ''), ' ', ''), char(9), ''), char(10), ''), char(13), ''), '/', ''))
                        WHERE "Id" = NEW."Id";
                    END;

                    CREATE TRIGGER "TR_Companies_NormalizeRegistrationIdentifier_Update"
                    AFTER UPDATE OF "Cnpj" ON "Companies"
                    BEGIN
                        UPDATE "Companies"
                        SET "NormalizedCnpj" = upper(replace(replace(replace(replace(replace(replace(replace(
                            COALESCE(NEW."Cnpj", ''), '.', ''), '-', ''), ' ', ''), char(9), ''), char(10), ''), char(13), ''), '/', ''))
                        WHERE "Id" = NEW."Id";
                    END;
                    """);
            }
            else
            {
                throw new NotSupportedException($"Provider de banco não suportado para normalização: {migrationBuilder.ActiveProvider}");
            }

            migrationBuilder.CreateIndex(
                name: "IX_Users_NormalizedCpf",
                table: "Users",
                column: "NormalizedCpf",
                unique: true,
                filter: "\"NormalizedCpf\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_Users_NormalizedEmail",
                table: "Users",
                column: "NormalizedEmail",
                unique: true,
                filter: "\"NormalizedEmail\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_Companies_NormalizedCnpj",
                table: "Companies",
                column: "NormalizedCnpj",
                unique: true,
                filter: "\"NormalizedCnpj\" <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql("""
                    DROP TRIGGER IF EXISTS "TR_Users_NormalizeRegistrationIdentifiers" ON "Users";
                    DROP TRIGGER IF EXISTS "TR_Companies_NormalizeRegistrationIdentifier" ON "Companies";
                    DROP FUNCTION IF EXISTS normalize_user_registration_identifiers();
                    DROP FUNCTION IF EXISTS normalize_company_registration_identifier();
                    """);
            }
            else if (migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
            {
                migrationBuilder.Sql("""
                    DROP TRIGGER IF EXISTS "TR_Users_NormalizeRegistrationIdentifiers_Insert";
                    DROP TRIGGER IF EXISTS "TR_Users_NormalizeRegistrationIdentifiers_Update";
                    DROP TRIGGER IF EXISTS "TR_Companies_NormalizeRegistrationIdentifier_Insert";
                    DROP TRIGGER IF EXISTS "TR_Companies_NormalizeRegistrationIdentifier_Update";
                    """);
            }

            migrationBuilder.DropIndex(
                name: "IX_Users_NormalizedCpf",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_NormalizedEmail",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Companies_NormalizedCnpj",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "NormalizedCpf",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "NormalizedEmail",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "NormalizedCnpj",
                table: "Companies");
        }
    }
}
