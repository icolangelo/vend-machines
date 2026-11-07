using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VendingMachines.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionProductSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SelectedProductIdSnapshot",
                table: "MachineSessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SelectedProductNameSnapshot",
                table: "MachineSessions",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new System.InvalidOperationException(
                "Esta migration preserva o produto histórico selecionado na sessão MDB. Reverta somente o binário e mantenha os snapshots gravados.");
        }
    }
}
