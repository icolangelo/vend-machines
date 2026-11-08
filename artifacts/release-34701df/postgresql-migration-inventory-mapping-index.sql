SET lock_timeout = '5s';
CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_InventoryMovements_MappingMismatch_Machine_Item_CreatedAt"
ON "InventoryMovements" ("MovementType", "MachineId", "ItemNumber", "CreatedAt")
WHERE "MovementType" = 'SaleMappingMismatch';

START TRANSACTION;
DO $verify_inventory_mapping_index$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_index i
        JOIN pg_class index_class ON index_class.oid = i.indexrelid
        JOIN pg_namespace index_schema ON index_schema.oid = index_class.relnamespace
        WHERE index_schema.nspname = 'public'
          AND index_class.relname = 'IX_InventoryMovements_MappingMismatch_Machine_Item_CreatedAt'
          AND i.indisvalid
          AND i.indisready
          AND pg_get_indexdef(i.indexrelid) ILIKE '%("MovementType", "MachineId", "ItemNumber", "CreatedAt")%'
          AND pg_get_expr(i.indpred, i.indrelid) ILIKE '%SaleMappingMismatch%'
    ) THEN
        RAISE EXCEPTION 'Índice parcial de alertas de estoque ausente ou inválido.';
    END IF;
END
$verify_inventory_mapping_index$;

COMMIT;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261026091223_IndexInventoryMappingMismatchAlerts', '10.0.12');

