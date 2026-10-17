using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VendingMachines.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTelemetryDeviceCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "TelemetryCredentialCreatedAt",
                table: "Machines",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TelemetryCredentialHash",
                table: "Machines",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TelemetryCredentialRevokedAt",
                table: "Machines",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TelemetryCredentialCreatedAt",
                table: "Machines");

            migrationBuilder.DropColumn(
                name: "TelemetryCredentialHash",
                table: "Machines");

            migrationBuilder.DropColumn(
                name: "TelemetryCredentialRevokedAt",
                table: "Machines");
        }
    }
}
