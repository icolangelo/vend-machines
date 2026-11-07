import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const { authenticatedFetch } = vi.hoisted(() => ({ authenticatedFetch: vi.fn() }));
vi.mock("@/lib/authClient", () => ({
    API_BASE_URL: "https://api.example.test/api",
    authenticatedFetch,
    getCurrentAuthUser: vi.fn(),
    SessionExpiredError: class extends Error {},
    SessionChangedError: class extends Error {},
}));

import { adjustInventorySlot, getInventoryMovementReceipt, InventoryMovementUncertainError, restockInventorySlot } from "@/lib/api";
import { SessionChangedError, SessionExpiredError } from "@/lib/authClient";

describe("movimentação de estoque", () => {
    beforeEach(() => {
        vi.stubEnv("VITE_IS_TEST_ENVIRONMENT", "false");
        authenticatedFetch.mockReset();
    });
    afterEach(() => vi.unstubAllEnvs());

    it("sends the same request identifier with the movement", async () => {
        authenticatedFetch.mockResolvedValue(new Response(null, { status: 200 }));
        await restockInventorySlot("machine-1", 2, 3, "Reposição", "b9d0ce8b-e9c2-43c5-b8e2-4f4f3e5a1001");
        const [, options] = authenticatedFetch.mock.calls[0];
        expect(JSON.parse(options.body)).toEqual({ quantity: 3, reason: "Reposição", requestId: "b9d0ce8b-e9c2-43c5-b8e2-4f4f3e5a1001" });
    });

    it.each([408, 500, 503])("treats HTTP %s as an uncertain outcome", async (status) => {
        authenticatedFetch.mockResolvedValue(new Response(null, { status }));
        await expect(adjustInventorySlot("machine-1", 2, -1, "Contagem")).rejects.toBeInstanceOf(InventoryMovementUncertainError);
    });

    it("treats a network failure as uncertain and preserves a clear rejection", async () => {
        authenticatedFetch.mockRejectedValueOnce(new TypeError("Failed to fetch"));
        await expect(restockInventorySlot("machine-1", 2, 3, "Reposição")).rejects.toBeInstanceOf(InventoryMovementUncertainError);
        authenticatedFetch.mockRejectedValueOnce(new DOMException("Aborted", "AbortError"));
        await expect(restockInventorySlot("machine-1", 2, 3, "Reposição")).rejects.toBeInstanceOf(InventoryMovementUncertainError);
        authenticatedFetch.mockResolvedValueOnce(new Response(JSON.stringify({ message: "Sessão MDB ativa." }), { status: 409, headers: { "Content-Type": "application/json" } }));
        await expect(restockInventorySlot("machine-1", 2, 3, "Reposição")).rejects.toThrow("Sessão MDB ativa.");
    });

    it("keeps an expired session as a known rejection", async () => {
        authenticatedFetch.mockRejectedValue(new SessionExpiredError());
        await expect(restockInventorySlot("machine-1", 2, 3, "Reposição")).rejects.toBeInstanceOf(SessionExpiredError);
    });

    it("treats an account change during a movement as an uncertain outcome", async () => {
        authenticatedFetch.mockRejectedValue(new SessionChangedError());
        await expect(restockInventorySlot("machine-1", 2, 3, "Reposição")).rejects.toBeInstanceOf(InventoryMovementUncertainError);
    });

    it("keeps a missing or unsupported receipt unconfirmed and rejects malformed replies", async () => {
        authenticatedFetch.mockResolvedValueOnce(new Response(null, { status: 404 }));
        await expect(getInventoryMovementReceipt("b9d0ce8b-e9c2-43c5-b8e2-4f4f3e5a1001")).resolves.toBeNull();
        authenticatedFetch.mockResolvedValueOnce(new Response(JSON.stringify({ machineId: "machine-1" }), { status: 200 }));
        await expect(getInventoryMovementReceipt("b9d0ce8b-e9c2-43c5-b8e2-4f4f3e5a1001")).rejects.toThrow("Resposta de movimentação inválida.");
    });
});
