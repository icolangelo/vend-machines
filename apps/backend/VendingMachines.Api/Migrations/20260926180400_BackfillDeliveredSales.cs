using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VendingMachines.Api.Migrations
{
    /// <inheritdoc />
    public partial class BackfillDeliveredSales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Origin",
                table: "SaleRecords",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "DeliveryConfirmed");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new System.InvalidOperationException(
                "Esta migration contém a origem auditável das vendas. Reverta somente o binário e preserve SaleRecords.Origin.");
        }
    }
}
