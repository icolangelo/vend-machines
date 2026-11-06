using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VendingMachines.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddLocationsToMachines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.AddColumn<Guid>(
                    name: "LocationId",
                    table: "Machines",
                    type: "uuid",
                    nullable: true);

                migrationBuilder.CreateTable(
                    name: "Locations",
                    columns: table => new
                    {
                        Id = table.Column<Guid>(type: "uuid", nullable: false),
                        CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                        Name = table.Column<string>(type: "text", nullable: false),
                        CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                        UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                    },
                    constraints: table =>
                    {
                        table.PrimaryKey("PK_Locations", x => x.Id);
                        table.ForeignKey(
                            name: "FK_Locations_Companies_CompanyId",
                            column: x => x.CompanyId,
                            principalTable: "Companies",
                            principalColumn: "Id",
                            onDelete: ReferentialAction.Cascade);
                    });

                migrationBuilder.Sql("""
INSERT INTO "Locations" ("Id", "CompanyId", "Name", "CreatedAt")
SELECT md5(random()::text || clock_timestamp()::text)::uuid, m."CompanyId", trim(m."ClientName"), now()
FROM "Machines" m
WHERE m."CompanyId" IS NOT NULL
  AND trim(m."ClientName") <> ''
GROUP BY m."CompanyId", trim(m."ClientName");

INSERT INTO "Locations" ("Id", "CompanyId", "Name", "CreatedAt")
SELECT md5(random()::text || clock_timestamp()::text)::uuid, c."Id", COALESCE(NULLIF(split_part(trim(c."Name"), ' ', 1), ''), 'Empresa') || ' - Shopping', now()
FROM "Companies" c
WHERE NOT EXISTS (
    SELECT 1 FROM "Locations" l
    WHERE l."CompanyId" = c."Id"
      AND l."Name" = COALESCE(NULLIF(split_part(trim(c."Name"), ' ', 1), ''), 'Empresa') || ' - Shopping'
);

INSERT INTO "Locations" ("Id", "CompanyId", "Name", "CreatedAt")
SELECT md5(random()::text || clock_timestamp()::text)::uuid, c."Id", COALESCE(NULLIF(split_part(trim(c."Name"), ' ', 1), ''), 'Empresa') || ' - Hospital', now()
FROM "Companies" c
WHERE NOT EXISTS (
    SELECT 1 FROM "Locations" l
    WHERE l."CompanyId" = c."Id"
      AND l."Name" = COALESCE(NULLIF(split_part(trim(c."Name"), ' ', 1), ''), 'Empresa') || ' - Hospital'
);

UPDATE "Machines" m
SET "LocationId" = l."Id",
    "ClientName" = l."Name"
FROM "Locations" l
WHERE m."CompanyId" = l."CompanyId"
  AND trim(m."ClientName") = l."Name"
  AND m."LocationId" IS NULL;

WITH first_location AS (
    SELECT DISTINCT ON ("CompanyId") "CompanyId", "Id", "Name"
    FROM "Locations"
    ORDER BY "CompanyId", "CreatedAt", "Name"
)
UPDATE "Machines" m
SET "LocationId" = fl."Id",
    "ClientName" = fl."Name"
FROM first_location fl
WHERE m."CompanyId" = fl."CompanyId"
  AND m."LocationId" IS NULL;
""");

                migrationBuilder.CreateIndex(
                    name: "IX_Machines_LocationId",
                    table: "Machines",
                    column: "LocationId");

                migrationBuilder.CreateIndex(
                    name: "IX_Locations_CompanyId_Name",
                    table: "Locations",
                    columns: new[] { "CompanyId", "Name" },
                    unique: true);

                migrationBuilder.AddForeignKey(
                    name: "FK_Machines_Locations_LocationId",
                    table: "Machines",
                    column: "LocationId",
                    principalTable: "Locations",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);

                return;
            }

            migrationBuilder.AddColumn<Guid>(
                name: "LocationId",
                table: "Machines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Locations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Locations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Locations_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("""
INSERT INTO "Locations" ("Id", "CompanyId", "Name", "CreatedAt")
SELECT lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-' || lower(hex(randomblob(2))) || '-' || lower(hex(randomblob(2))) || '-' || lower(hex(randomblob(6))),
       m."CompanyId",
       trim(m."ClientName"),
       CURRENT_TIMESTAMP
FROM "Machines" m
WHERE m."CompanyId" IS NOT NULL
  AND trim(m."ClientName") <> ''
GROUP BY m."CompanyId", trim(m."ClientName");

INSERT INTO "Locations" ("Id", "CompanyId", "Name", "CreatedAt")
SELECT lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-' || lower(hex(randomblob(2))) || '-' || lower(hex(randomblob(2))) || '-' || lower(hex(randomblob(6))),
       c."Id",
       COALESCE(NULLIF(substr(trim(c."Name"), 1, instr(trim(c."Name") || ' ', ' ') - 1), ''), 'Empresa') || ' - Shopping',
       CURRENT_TIMESTAMP
FROM "Companies" c
WHERE NOT EXISTS (
    SELECT 1 FROM "Locations" l
    WHERE l."CompanyId" = c."Id"
      AND l."Name" = COALESCE(NULLIF(substr(trim(c."Name"), 1, instr(trim(c."Name") || ' ', ' ') - 1), ''), 'Empresa') || ' - Shopping'
);

INSERT INTO "Locations" ("Id", "CompanyId", "Name", "CreatedAt")
SELECT lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-' || lower(hex(randomblob(2))) || '-' || lower(hex(randomblob(2))) || '-' || lower(hex(randomblob(6))),
       c."Id",
       COALESCE(NULLIF(substr(trim(c."Name"), 1, instr(trim(c."Name") || ' ', ' ') - 1), ''), 'Empresa') || ' - Hospital',
       CURRENT_TIMESTAMP
FROM "Companies" c
WHERE NOT EXISTS (
    SELECT 1 FROM "Locations" l
    WHERE l."CompanyId" = c."Id"
      AND l."Name" = COALESCE(NULLIF(substr(trim(c."Name"), 1, instr(trim(c."Name") || ' ', ' ') - 1), ''), 'Empresa') || ' - Hospital'
);

UPDATE "Machines"
SET "LocationId" = (
        SELECT l."Id"
        FROM "Locations" l
        WHERE l."CompanyId" = "Machines"."CompanyId"
          AND l."Name" = trim("Machines"."ClientName")
        LIMIT 1
    ),
    "ClientName" = (
        SELECT l."Name"
        FROM "Locations" l
        WHERE l."CompanyId" = "Machines"."CompanyId"
          AND l."Name" = trim("Machines"."ClientName")
        LIMIT 1
    )
WHERE "LocationId" IS NULL
  AND EXISTS (
      SELECT 1 FROM "Locations" l
      WHERE l."CompanyId" = "Machines"."CompanyId"
        AND l."Name" = trim("Machines"."ClientName")
  );

UPDATE "Machines"
SET "LocationId" = (
        SELECT l."Id"
        FROM "Locations" l
        WHERE l."CompanyId" = "Machines"."CompanyId"
        ORDER BY l."CreatedAt", l."Name"
        LIMIT 1
    ),
    "ClientName" = (
        SELECT l."Name"
        FROM "Locations" l
        WHERE l."CompanyId" = "Machines"."CompanyId"
        ORDER BY l."CreatedAt", l."Name"
        LIMIT 1
    )
WHERE "LocationId" IS NULL
  AND EXISTS (
      SELECT 1 FROM "Locations" l
      WHERE l."CompanyId" = "Machines"."CompanyId"
  );
""");

            migrationBuilder.CreateIndex(
                name: "IX_Machines_LocationId",
                table: "Machines",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_Locations_CompanyId_Name",
                table: "Locations",
                columns: new[] { "CompanyId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Machines_Locations_LocationId",
                table: "Machines",
                column: "LocationId",
                principalTable: "Locations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Machines_Locations_LocationId",
                table: "Machines");

            migrationBuilder.DropTable(
                name: "Locations");

            migrationBuilder.DropIndex(
                name: "IX_Machines_LocationId",
                table: "Machines");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "Machines");
        }
    }
}
