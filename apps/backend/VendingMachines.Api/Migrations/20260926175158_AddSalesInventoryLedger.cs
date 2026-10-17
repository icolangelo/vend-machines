using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VendingMachines.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSalesInventoryLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MachineProductSlots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    MachineId = table.Column<string>(type: "text", nullable: false),
                    ItemNumber = table.Column<int>(type: "integer", nullable: false),
                    ProductId = table.Column<string>(type: "text", nullable: false),
                    Capacity = table.Column<int>(type: "integer", nullable: false),
                    CurrentStock = table.Column<int>(type: "integer", nullable: false),
                    LowStockThreshold = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MachineProductSlots", x => x.Id);
                    table.UniqueConstraint("AK_MachineProductSlots_MachineId_ItemNumber", x => new { x.MachineId, x.ItemNumber });
                    table.ForeignKey(
                        name: "FK_MachineProductSlots_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MachineProductSlots_Machines_MachineId",
                        column: x => x.MachineId,
                        principalTable: "Machines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MachineProductSlots_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SaleRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    MachineId = table.Column<string>(type: "text", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TransactionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemNumber = table.Column<int>(type: "integer", nullable: false),
                    ProductId = table.Column<string>(type: "text", nullable: true),
                    ProductNameSnapshot = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    GrossAmount = table.Column<decimal>(type: "numeric", nullable: false),
                    ApplicationFee = table.Column<decimal>(type: "numeric", nullable: false),
                    NetAmount = table.Column<decimal>(type: "numeric", nullable: false),
                    SoldAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SaleRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SaleRecords_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SaleRecords_MachineSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "MachineSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SaleRecords_Machines_MachineId",
                        column: x => x.MachineId,
                        principalTable: "Machines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SaleRecords_PaymentTransactions_TransactionId",
                        column: x => x.TransactionId,
                        principalTable: "PaymentTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SaleRecords_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "InventoryMovements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    MachineId = table.Column<string>(type: "text", nullable: false),
                    ItemNumber = table.Column<int>(type: "integer", nullable: false),
                    ProductId = table.Column<string>(type: "text", nullable: false),
                    SaleRecordId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    MovementType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    QuantityDelta = table.Column<int>(type: "integer", nullable: false),
                    QuantityAfter = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryMovements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventoryMovements_MachineProductSlots_MachineId_ItemNumber",
                        columns: x => new { x.MachineId, x.ItemNumber },
                        principalTable: "MachineProductSlots",
                        principalColumns: new[] { "MachineId", "ItemNumber" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryMovements_SaleRecords_SaleRecordId",
                        column: x => x.SaleRecordId,
                        principalTable: "SaleRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_MachineId_ItemNumber_CreatedAt",
                table: "InventoryMovements",
                columns: new[] { "MachineId", "ItemNumber", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_SaleRecordId",
                table: "InventoryMovements",
                column: "SaleRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_MachineProductSlots_CompanyId_ProductId",
                table: "MachineProductSlots",
                columns: new[] { "CompanyId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_MachineProductSlots_MachineId_ItemNumber",
                table: "MachineProductSlots",
                columns: new[] { "MachineId", "ItemNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MachineProductSlots_ProductId",
                table: "MachineProductSlots",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleRecords_CompanyId_ProductId_SoldAt",
                table: "SaleRecords",
                columns: new[] { "CompanyId", "ProductId", "SoldAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SaleRecords_CompanyId_SoldAt",
                table: "SaleRecords",
                columns: new[] { "CompanyId", "SoldAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SaleRecords_MachineId",
                table: "SaleRecords",
                column: "MachineId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleRecords_ProductId",
                table: "SaleRecords",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleRecords_SessionId",
                table: "SaleRecords",
                column: "SessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SaleRecords_TransactionId",
                table: "SaleRecords",
                column: "TransactionId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InventoryMovements");

            migrationBuilder.DropTable(
                name: "MachineProductSlots");

            migrationBuilder.DropTable(
                name: "SaleRecords");
        }
    }
}
