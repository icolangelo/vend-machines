using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VendingMachines.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddWebhookInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WebhookInboxMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PaymentId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    EventType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EventAction = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Format = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AuthenticationMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ProcessingStartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ProcessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookInboxMessages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookInboxMessages_EventKey",
                table: "WebhookInboxMessages",
                column: "EventKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebhookInboxMessages_Status_NextAttemptAt_ReceivedAt",
                table: "WebhookInboxMessages",
                columns: new[] { "Status", "NextAttemptAt", "ReceivedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WebhookInboxMessages");
        }
    }
}
