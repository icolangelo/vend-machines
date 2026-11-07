#!/usr/bin/env python3
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path


MODULE_PATH = Path(__file__).with_name("check_migration_risk.py")
SPEC = importlib.util.spec_from_file_location("check_migration_risk", MODULE_PATH)
checker = importlib.util.module_from_spec(SPEC)
assert SPEC and SPEC.loader
SPEC.loader.exec_module(checker)


class MigrationRiskTests(unittest.TestCase):
    def test_destructive_down_method_does_not_mark_additive_up_method_risky(self):
        source = """
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.CreateTable(name: "Items", columns: table => new { Id = table.Column<int>() });
    migrationBuilder.AddColumn<string>(name: "NewValue", table: "Items", nullable: true);
}
protected override void Down(MigrationBuilder migrationBuilder)
{
    migrationBuilder.DropColumn(name: "NewValue", table: "Items");
}
"""
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "20261024000000_AddValue.cs"
            path.write_text(source, encoding="utf-8")
            risks, _, _, _, _ = checker.migration_risks(path)
        self.assertEqual(set(), risks)

    def test_sql_and_drop_operations_in_up_are_risks(self):
        source = """
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.Sql("DELETE FROM \\\"Items\\\";");
    migrationBuilder.DropColumn(name: "OldValue", table: "Items");
}
protected override void Down(MigrationBuilder migrationBuilder) { }
"""
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "20261024000000_ChangeItems.cs"
            path.write_text(source, encoding="utf-8")
            risks, _, _, _, _ = checker.migration_risks(path)
        self.assertEqual({"Sql", "DropColumn"}, risks)

    def test_trigger_sql_tracks_the_existing_table_after_on(self):
        source = r'''
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.Sql("""
        CREATE TRIGGER \"PauseGate\" BEFORE INSERT ON \"Machines\"
        FOR EACH ROW SELECT \"Enabled\" FROM \"SessionAdmissionGates\";
        """);
}
protected override void Down(MigrationBuilder migrationBuilder) { }
'''
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "20261024000000_GateInsertTrigger.cs"
            path.write_text(source, encoding="utf-8")
            risks, _, _, affected_tables, _ = checker.migration_risks(
                path, {"Machines", "SessionAdmissionGates"})
        self.assertEqual({"Sql"}, risks)
        self.assertEqual({"Machines", "SessionAdmissionGates"}, affected_tables)

    def test_unlisted_drop_operation_is_still_a_risk(self):
        source = "protected override void Up(MigrationBuilder migrationBuilder) { migrationBuilder.DropSequence(\"old_ids\"); }"
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "20261024000000_RemoveSequence.cs"
            path.write_text(source, encoding="utf-8")
            risks, _, _, _, _ = checker.migration_risks(path)
        self.assertEqual({"DropSequence"}, risks)

    def test_unknown_migration_builder_operation_fails_closed(self):
        source = '''
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.AlterSequence("legacy_ids");
}
protected override void Down(MigrationBuilder migrationBuilder) { }
'''
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "20261102000002_AlterSequence.cs"
            path.write_text(source, encoding="utf-8")
            with self.assertRaisesRegex(checker.MigrationReviewError, "sem classificação.*AlterSequence"):
                checker.migration_risks(path)

    def test_only_index_creation_on_a_preexisting_table_is_a_lock_risk(self):
        source = """
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.CreateTable(name: "Fresh", columns: table => new { Id = table.Column<int>() });
    migrationBuilder.CreateIndex(name: "IX_Fresh_Id", table: "Fresh", column: "Id");
    migrationBuilder.CreateIndex(name: "IX_Machines_Name", table: "Machines", column: "Name");
}
protected override void Down(MigrationBuilder migrationBuilder) { }
"""
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "20261024000000_AddIndexes.cs"
            path.write_text(source, encoding="utf-8")
            risks, index_tables, created_tables, affected_tables, _ = checker.migration_risks(path, {"Machines"})
        self.assertEqual({"CreateIndex"}, risks)
        self.assertEqual({"Machines"}, index_tables)
        self.assertEqual({"Fresh"}, created_tables)
        self.assertEqual({"Machines"}, affected_tables)

    def test_generic_add_column_on_existing_table_is_a_risk(self):
        source = """
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.CreateTable(name: "Fresh", columns: table => new { Id = table.Column<int>() });
    migrationBuilder.AddColumn<string>(name: "Description", table: "Fresh", nullable: true);
    migrationBuilder.AddColumn<int>(name: "Priority", table: "Machines", nullable: false, defaultValue: 0);
}
protected override void Down(MigrationBuilder migrationBuilder) { }
"""
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "20261024000000_AddMachinePriority.cs"
            path.write_text(source, encoding="utf-8")
            risks, _, _, affected_tables, _ = checker.migration_risks(path, {"Machines"})
        self.assertEqual({"AddColumn"}, risks)
        self.assertEqual({"Machines"}, affected_tables)

    def test_adding_foreign_key_to_existing_table_is_a_risk(self):
        source = """
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.AddForeignKey(
        name: "FK_Orders_Customers",
        table: "Orders",
        column: "CustomerId",
        principalTable: "Customers",
        principalColumn: "Id",
        onDelete: ReferentialAction.Cascade);
}
protected override void Down(MigrationBuilder migrationBuilder) { }
"""
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "20261024000000_AddCustomerForeignKey.cs"
            path.write_text(source, encoding="utf-8")
            risks, _, _, affected_tables, _ = checker.migration_risks(path, {"Orders", "Customers"})
        self.assertEqual({"AddForeignKey"}, risks)
        self.assertEqual({"Orders", "Customers"}, affected_tables)

    def test_adding_check_constraint_to_existing_table_is_a_risk(self):
        source = '''
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.AddCheckConstraint(
        name: "CK_Machines_PositiveCapacity",
        table: "Machines",
        sql: "\\"Capacity\\" > 0");
}
protected override void Down(MigrationBuilder migrationBuilder) { }
'''
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "20261102000000_AddMachineCapacityCheck.cs"
            path.write_text(source, encoding="utf-8")
            risks, _, _, affected_tables, _ = checker.migration_risks(path, {"Machines"})
        self.assertEqual({"AddCheckConstraint"}, risks)
        self.assertEqual({"Machines"}, affected_tables)

    def test_check_constraint_on_table_created_in_same_migration_is_not_a_populated_table_risk(self):
        source = '''
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.CreateTable(name: "Fresh", columns: table => new { Id = table.Column<int>() });
    migrationBuilder.AddCheckConstraint(
        name: "CK_Fresh_PositiveCapacity",
        table: "Fresh",
        sql: "\\"Capacity\\" > 0");
}
protected override void Down(MigrationBuilder migrationBuilder) { }
'''
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "20261102000001_AddFreshCapacityCheck.cs"
            path.write_text(source, encoding="utf-8")
            risks, _, _, affected_tables, _ = checker.migration_risks(path, set())
        self.assertEqual(set(), risks)
        self.assertEqual(set(), affected_tables)

    def test_table_operation_on_table_missing_from_migration_history_fails_closed(self):
        source = '''
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.AddCheckConstraint(
        name: "CK_Legacy_PositiveCapacity",
        table: "UntrackedLegacyTable",
        sql: "\\"Capacity\\" > 0");
}
protected override void Down(MigrationBuilder migrationBuilder) { }
'''
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "20261102000003_AddLegacyCapacityCheck.cs"
            path.write_text(source, encoding="utf-8")
            with self.assertRaisesRegex(checker.MigrationReviewError, "tabela 'UntrackedLegacyTable' sem origem"):
                checker.migration_risks(path)

    def test_foreign_key_principal_missing_from_migration_history_fails_closed(self):
        source = '''
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.AddForeignKey(
        name: "FK_Orders_UnknownCustomers",
        table: "Orders",
        column: "CustomerId",
        principalTable: "UnknownCustomers",
        principalColumn: "Id");
}
protected override void Down(MigrationBuilder migrationBuilder) { }
'''
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "20261102000004_AddUnknownCustomerForeignKey.cs"
            path.write_text(source, encoding="utf-8")
            with self.assertRaisesRegex(checker.MigrationReviewError, "UnknownCustomers.*sem origem"):
                checker.migration_risks(path, {"Orders"})

    def test_creating_table_with_foreign_key_to_existing_table_is_a_lock_risk(self):
        source = '''
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.CreateTable(
        name: "Orders",
        columns: table => new { Id = table.Column<int>() },
        constraints: table =>
        {
            table.PrimaryKey("PK_Orders", x => x.Id);
            table.ForeignKey(
                name: "FK_Orders_Customers",
                column: x => x.CustomerId,
                principalTable: "Customers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        });
}
protected override void Down(MigrationBuilder migrationBuilder) { }
'''
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "20261024000000_AddOrders.cs"
            path.write_text(source, encoding="utf-8")
            risks, _, created_tables, affected_tables, _ = checker.migration_risks(path, {"Customers"})
        self.assertEqual({"CreateTableForeignKey"}, risks)
        self.assertEqual({"Orders"}, created_tables)
        self.assertEqual({"Customers"}, affected_tables)

    def test_foreign_key_to_table_created_in_same_migration_is_not_a_populated_table_lock_risk(self):
        source = '''
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.CreateTable(
        name: "Customers",
        columns: table => new { Id = table.Column<int>() });
    migrationBuilder.CreateTable(
        name: "Orders",
        columns: table => new { Id = table.Column<int>() },
        constraints: table => table.ForeignKey(
            name: "FK_Orders_Customers",
            column: x => x.CustomerId,
            principalTable: "Customers",
            principalColumn: "Id"));
}
protected override void Down(MigrationBuilder migrationBuilder) { }
'''
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "20261024000000_AddCustomerOrders.cs"
            path.write_text(source, encoding="utf-8")
            risks, _, created_tables, affected_tables, _ = checker.migration_risks(path, set())
        self.assertEqual(set(), risks)
        self.assertEqual({"Customers", "Orders"}, created_tables)
        self.assertEqual(set(), affected_tables)

    def test_sql_operations_record_existing_tables_referenced_by_sql(self):
        source = '''
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.Sql("""
        UPDATE "Machines" SET "IsActive" = TRUE WHERE "Id" = 'legacy';
        INSERT INTO "AuditLogs" ("Action") SELECT 'migration' FROM "Users";
        CREATE TEMP TABLE "Scratch" ("Id" TEXT);
        """);
    migrationBuilder.Sql("DELETE FROM \\\"Integrations\\\" WHERE \\\"ClientId\\\" IS NULL;");
}
protected override void Down(MigrationBuilder migrationBuilder) { }
'''
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "20261024000000_UpdateLegacyMachines.cs"
            path.write_text(source, encoding="utf-8")
            risks, _, _, affected_tables, _ = checker.migration_risks(
                path,
                {"Machines", "AuditLogs", "Users", "Integrations"},
            )
        self.assertEqual({"Sql"}, risks)
        self.assertEqual({"Machines", "AuditLogs", "Users", "Integrations"}, affected_tables)

    def test_review_requires_exact_preexisting_index_targets(self):
        source = """
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.CreateIndex(name: "IX_Machines_Name", table: "Machines", column: "Name");
}
protected override void Down(MigrationBuilder migrationBuilder) { }
"""
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "20261023000000_CreateMachines.cs").write_text(
                'protected override void Up(MigrationBuilder migrationBuilder) { migrationBuilder.CreateTable(name: "Machines", columns: table => new { Id = table.Column<int>() }); }',
                encoding="utf-8",
            )
            migration = root / "20261024000000_AddMachineIndex.cs"
            migration.write_text(source, encoding="utf-8")
            risks, index_tables, _, affected_tables, normalized = checker.migration_risks(migration, {"Machines"})
            manifest = root / "reviews.json"
            entry = {
                "operations": sorted(risks),
                "index_tables": sorted(index_tables),
                "affected_tables": sorted(affected_tables),
                "impact": "Cria índice em tabela usada pela aplicação e pode disputar lock com gravações ativas.",
                "recovery": "Medir em cópia e preservar o schema expandido em caso de rollback da aplicação.",
                "source_sha256": checker.source_digest(normalized),
            }
            manifest.write_text(json.dumps({migration.stem: entry}), encoding="utf-8")
            self.assertEqual([(migration.stem, ["CreateIndex"])], checker.validate_reviews(root, manifest))

            entry["index_tables"] = []
            manifest.write_text(json.dumps({migration.stem: entry}), encoding="utf-8")
            with self.assertRaisesRegex(checker.MigrationReviewError, "tabelas com novos índices divergem"):
                checker.validate_reviews(root, manifest)

            entry["index_tables"] = ["Machines"]
            entry["affected_tables"] = []
            manifest.write_text(json.dumps({migration.stem: entry}), encoding="utf-8")
            with self.assertRaisesRegex(checker.MigrationReviewError, "tabelas afetadas divergem"):
                checker.validate_reviews(root, manifest)

    def test_requires_a_matching_review_hash_and_recovery_note(self):
        source = """
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.DeleteData("Items", "Id", "old");
}
protected override void Down(MigrationBuilder migrationBuilder) { }
"""
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            migrations = root / "migrations"
            migrations.mkdir()
            migration = migrations / "20261024000000_RemoveOldItems.cs"
            migration.write_text(source, encoding="utf-8")
            risks, _, _, _, normalized = checker.migration_risks(migration)
            manifest = root / "reviews.json"
            manifest.write_text(json.dumps({migration.stem: {
                "operations": sorted(risks),
                "affected_tables": [],
                "impact": "Remove somente registros legados previamente identificados e contabilizados.",
                "recovery": "Restaurar os registros a partir do backup aprovado antes de retomar a operação.",
                "source_sha256": checker.source_digest(normalized),
            }}), encoding="utf-8")
            self.assertEqual([(migration.stem, ["DeleteData"])], checker.validate_reviews(migrations, manifest))

            manifest.write_text("{}", encoding="utf-8")
            with self.assertRaisesRegex(checker.MigrationReviewError, "sem revisão registrada"):
                checker.validate_reviews(migrations, manifest)

    def test_detects_stale_hash_after_reviewed_migration_changes(self):
        source = "protected override void Up(MigrationBuilder migrationBuilder) { migrationBuilder.Sql(\"UPDATE Items SET Active = false\"); }"
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            migration = root / "20261024000000_UpdateItems.cs"
            migration.write_text(source, encoding="utf-8")
            manifest = root / "reviews.json"
            manifest.write_text(json.dumps({migration.stem: {
                "operations": ["Sql"],
                "impact": "Desativa dados legados que foram identificados por um critério revisado.",
                "recovery": "Usar o backup da base antes da migration para reverter a alteração dos dados.",
                "source_sha256": "0" * 64,
            }}), encoding="utf-8")
            with self.assertRaisesRegex(checker.MigrationReviewError, "mudou desde a revisão"):
                checker.validate_reviews(root, manifest)


if __name__ == "__main__":
    unittest.main()
