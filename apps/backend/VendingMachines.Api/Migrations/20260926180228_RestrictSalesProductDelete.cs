using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VendingMachines.Api.Migrations
{
    /// <inheritdoc />
    public partial class RestrictSalesProductDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SaleRecords_Products_ProductId",
                table: "SaleRecords");

            migrationBuilder.AddForeignKey(
                name: "FK_SaleRecords_Products_ProductId",
                table: "SaleRecords",
                column: "ProductId",
                principalTable: "Products",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SaleRecords_Products_ProductId",
                table: "SaleRecords");

            migrationBuilder.AddForeignKey(
                name: "FK_SaleRecords_Products_ProductId",
                table: "SaleRecords",
                column: "ProductId",
                principalTable: "Products",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
