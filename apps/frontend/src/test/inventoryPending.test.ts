import { createHash, webcrypto } from "node:crypto";
import { afterEach, describe, expect, it, vi } from "vitest";
import { hashInventoryReason, inventoryPendingKey, readPendingInventoryMovement } from "@/lib/inventoryPending";

describe("estado pendente de estoque", () => {
    afterEach(() => {
        sessionStorage.clear();
        vi.unstubAllGlobals();
    });

    it("hashes UTF-8 reason text in the same form used by the API", async () => {
        vi.stubGlobal("crypto", webcrypto);
        const reason = "Reposição sintética";
        expect(await hashInventoryReason(reason)).toBe(createHash("sha256").update(reason, "utf8").digest("hex"));
    });

    it("keeps old markers without receipt metadata and fails closed on corrupt metadata", () => {
        const key = inventoryPendingKey("user-1", "company-1");
        const marker = { machineId: "machine-1", itemNumber: 1, movementType: "Restock", quantity: 2, startedAt: "2026-10-29T12:00:00.000Z" };
        sessionStorage.setItem(key, JSON.stringify(marker));
        expect(readPendingInventoryMovement(key)).toEqual(marker);
        sessionStorage.setItem(key, JSON.stringify({ ...marker, reasonHash: "invalid" }));
        expect(() => readPendingInventoryMovement(key)).toThrow("Estado de movimentação inválido.");
    });
});
