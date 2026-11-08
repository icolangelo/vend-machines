-- Execute somente esta instrução, com auto-commit ligado, sem transação adicional.
CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_InventoryMovements_MappingMismatch_Machine_Item_CreatedAt"
ON "InventoryMovements" ("MovementType", "MachineId", "ItemNumber", "CreatedAt")
WHERE "MovementType" = 'SaleMappingMismatch';
