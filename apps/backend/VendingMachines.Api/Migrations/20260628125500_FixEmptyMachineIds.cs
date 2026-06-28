using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VendingMachines.Api.Migrations
{
    /// <inheritdoc />
    public partial class FixEmptyMachineIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql("""
DO $$
DECLARE
    new_id text;
BEGIN
    IF EXISTS (SELECT 1 FROM "Machines" WHERE "Id" = '') THEN
        new_id := 'VM-' || upper(substr(md5(random()::text || clock_timestamp()::text), 1, 8));

        WHILE EXISTS (SELECT 1 FROM "Machines" WHERE "Id" = new_id) LOOP
            new_id := 'VM-' || upper(substr(md5(random()::text || clock_timestamp()::text), 1, 8));
        END LOOP;

        ALTER TABLE "PaymentTransactions" DROP CONSTRAINT IF EXISTS "FK_PaymentTransactions_Machines_MachineId";
        UPDATE "Machines" SET "Id" = new_id WHERE "Id" = '';
        UPDATE "PaymentTransactions" SET "MachineId" = new_id WHERE "MachineId" = '';
        ALTER TABLE "PaymentTransactions"
            ADD CONSTRAINT "FK_PaymentTransactions_Machines_MachineId"
            FOREIGN KEY ("MachineId") REFERENCES "Machines" ("Id") ON DELETE CASCADE;
    END IF;
END $$;
""");
                return;
            }

            migrationBuilder.Sql("""
PRAGMA foreign_keys=OFF;

CREATE TEMP TABLE IF NOT EXISTS "__MachineIdFix" (
    "OldId" TEXT PRIMARY KEY,
    "NewId" TEXT NOT NULL
);

DELETE FROM "__MachineIdFix";

INSERT INTO "__MachineIdFix" ("OldId", "NewId")
SELECT "Id", 'VM-' || lower(hex(randomblob(4)))
FROM "Machines"
WHERE "Id" = '';

UPDATE "Machines"
SET "Id" = (SELECT "NewId" FROM "__MachineIdFix" WHERE "OldId" = '')
WHERE "Id" = ''
  AND EXISTS (SELECT 1 FROM "__MachineIdFix" WHERE "OldId" = '');

UPDATE "PaymentTransactions"
SET "MachineId" = (SELECT "NewId" FROM "__MachineIdFix" WHERE "OldId" = '')
WHERE "MachineId" = ''
  AND EXISTS (SELECT 1 FROM "__MachineIdFix" WHERE "OldId" = '');

DROP TABLE "__MachineIdFix";

PRAGMA foreign_keys=ON;
""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
