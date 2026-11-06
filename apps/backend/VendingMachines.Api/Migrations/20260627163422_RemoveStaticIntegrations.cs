using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VendingMachines.Api.Migrations
{
    /// <inheritdoc />
    public partial class RemoveStaticIntegrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM \"MercadoPagoIntegrations\" WHERE \"ClientId\" IS NULL OR \"ClientId\" = '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new System.InvalidOperationException(
                "Esta migration remove integrações sem ClientId e não consegue recriá-las no Down. O Down agora falha fechado; reverta somente o binário e, se necessário, recupere os registros de backup validado.");

        }
    }
}
