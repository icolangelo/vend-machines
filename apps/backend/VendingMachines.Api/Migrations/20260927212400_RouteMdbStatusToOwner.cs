using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VendingMachines.Api.Migrations
{
    /// <inheritdoc />
    public partial class RouteMdbStatusToOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Operation",
                table: "TelemetryCommandDispatches",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "Command");

            migrationBuilder.AddColumn<DateTime>(
                name: "ResponseAt",
                table: "TelemetryCommandDispatches",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResponseDataJson",
                table: "TelemetryCommandDispatches",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResponseStatus",
                table: "TelemetryCommandDispatches",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TimeoutMilliseconds",
                table: "TelemetryCommandDispatches",
                type: "integer",
                nullable: false,
                defaultValue: 15000);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new System.InvalidOperationException(
                "Esta migration contém despachos e resultados MDB persistidos. Reverta somente o binário e preserve as colunas do dispatch.");
        }
    }
}
