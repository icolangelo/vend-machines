using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VendingMachines.Api.Migrations
{
    /// <inheritdoc />
    public partial class TrackSaleRefunds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FinancialStatus",
                table: "SaleRecords",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Settled");

            migrationBuilder.AddColumn<decimal>(
                name: "RefundedAmount",
                table: "SaleRecords",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FinancialStatus",
                table: "SaleRecords");

            migrationBuilder.DropColumn(
                name: "RefundedAmount",
                table: "SaleRecords");
        }
    }
}
