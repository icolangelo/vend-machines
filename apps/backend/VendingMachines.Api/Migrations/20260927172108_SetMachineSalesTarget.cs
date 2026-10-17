using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VendingMachines.Api.Migrations
{
    /// <inheritdoc />
    public partial class SetMachineSalesTarget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MinimumSalesTarget30d",
                table: "Machines",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MinimumSalesTarget30d",
                table: "Machines");
        }
    }
}
