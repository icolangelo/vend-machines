using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VendingMachines.Api.Migrations
{
    /// <inheritdoc />
    public partial class EnforceSystemSettingsSingleton : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSingleton",
                table: "SystemSettings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateIndex(
                name: "IX_SystemSettings_IsSingleton",
                table: "SystemSettings",
                column: "IsSingleton",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_SystemSettings_Singleton",
                table: "SystemSettings",
                sql: "\"IsSingleton\" = TRUE");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new System.InvalidOperationException(
                "Esta migration protege a configuração global de taxa contra duplicatas. Reverta somente o binário e preserve a restrição singleton.");
        }
    }
}
