using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VendingMachines.Api.Migrations
{
    /// <inheritdoc />
    public partial class TrackMdbMenuSequence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MdbMenuConnectionId",
                table: "MachineConnectionStates",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MdbMenuPhase",
                table: "MachineConnectionStates",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MdbMenuSessionId",
                table: "MachineConnectionStates",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MdbMenuUpdatedAt",
                table: "MachineConnectionStates",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MdbMenuConnectionId",
                table: "MachineConnectionStates");

            migrationBuilder.DropColumn(
                name: "MdbMenuPhase",
                table: "MachineConnectionStates");

            migrationBuilder.DropColumn(
                name: "MdbMenuSessionId",
                table: "MachineConnectionStates");

            migrationBuilder.DropColumn(
                name: "MdbMenuUpdatedAt",
                table: "MachineConnectionStates");
        }
    }
}
