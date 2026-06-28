using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VendingMachines.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMercadoPagoOAuthDiagnostics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.AddColumn<DateTime>(
                    name: "AccessTokenExpiresAt",
                    table: "MercadoPagoIntegrations",
                    type: "timestamp with time zone",
                    nullable: true);

                migrationBuilder.AddColumn<DateTime>(
                    name: "LastTokenValidationAt",
                    table: "MercadoPagoIntegrations",
                    type: "timestamp with time zone",
                    nullable: true);

                migrationBuilder.AddColumn<string>(
                    name: "LastTokenValidationStatus",
                    table: "MercadoPagoIntegrations",
                    type: "text",
                    nullable: false,
                    defaultValue: "");

                migrationBuilder.AddColumn<string>(
                    name: "MercadoPagoNickname",
                    table: "MercadoPagoIntegrations",
                    type: "text",
                    nullable: false,
                    defaultValue: "");

                migrationBuilder.AddColumn<string>(
                    name: "MercadoPagoSiteId",
                    table: "MercadoPagoIntegrations",
                    type: "text",
                    nullable: false,
                    defaultValue: "");

                migrationBuilder.AddColumn<string>(
                    name: "MercadoPagoUserId",
                    table: "MercadoPagoIntegrations",
                    type: "text",
                    nullable: false,
                    defaultValue: "");

                migrationBuilder.AddColumn<string>(
                    name: "RefreshToken",
                    table: "MercadoPagoIntegrations",
                    type: "text",
                    nullable: false,
                    defaultValue: "");

                return;
            }

            migrationBuilder.AddColumn<DateTime>(
                name: "AccessTokenExpiresAt",
                table: "MercadoPagoIntegrations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastTokenValidationAt",
                table: "MercadoPagoIntegrations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastTokenValidationStatus",
                table: "MercadoPagoIntegrations",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "MercadoPagoNickname",
                table: "MercadoPagoIntegrations",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "MercadoPagoSiteId",
                table: "MercadoPagoIntegrations",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "MercadoPagoUserId",
                table: "MercadoPagoIntegrations",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RefreshToken",
                table: "MercadoPagoIntegrations",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccessTokenExpiresAt",
                table: "MercadoPagoIntegrations");

            migrationBuilder.DropColumn(
                name: "LastTokenValidationAt",
                table: "MercadoPagoIntegrations");

            migrationBuilder.DropColumn(
                name: "LastTokenValidationStatus",
                table: "MercadoPagoIntegrations");

            migrationBuilder.DropColumn(
                name: "MercadoPagoNickname",
                table: "MercadoPagoIntegrations");

            migrationBuilder.DropColumn(
                name: "MercadoPagoSiteId",
                table: "MercadoPagoIntegrations");

            migrationBuilder.DropColumn(
                name: "MercadoPagoUserId",
                table: "MercadoPagoIntegrations");

            migrationBuilder.DropColumn(
                name: "RefreshToken",
                table: "MercadoPagoIntegrations");
        }
    }
}
