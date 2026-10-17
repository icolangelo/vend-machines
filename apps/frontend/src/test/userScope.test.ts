import { afterEach, describe, expect, it, vi } from "vitest";
import { clearAuthSession, setAuthSession } from "@/lib/authClient";
import { getUsers, updateUserActiveStatus, updateUserRole } from "@/lib/api";

const ACME_ID = "11111111-1111-1111-1111-111111111111";
const SABOR_ID = "55555555-5555-5555-5555-555555555555";

function signIn(role: string, companyId: string, id = "usr-46") {
    setAuthSession({
        token: "test-token",
        expiresAt: new Date(Date.now() + 60_000).toISOString(),
        user: { id, name: "Admin de teste", email: "admin@example.test", role, companyId },
    });
}

describe("escopo de usuários no modo de teste", () => {
    afterEach(() => {
        clearAuthSession("logout");
        sessionStorage.clear();
        vi.unstubAllEnvs();
    });

    it("impede Admin de alterar usuário de outra empresa mesmo conhecendo o ID", async () => {
        vi.stubEnv("VITE_IS_TEST_ENVIRONMENT", "true");
        signIn("Admin", SABOR_ID);

        const users = await getUsers();
        expect(users.items.map(user => user.id)).toContain("usr-2");
        expect(users.items.map(user => user.id)).not.toContain("usr-1");
        await expect(updateUserRole("usr-1", "Admin")).rejects.toThrow("escopo permitido");
        await expect(updateUserActiveStatus("usr-1", false)).rejects.toThrow("escopo permitido");
        await expect(updateUserRole("usr-2", "Admin")).resolves.toBeUndefined();
    });

    it("limita o PlatformAdmin à empresa selecionada durante impersonação", async () => {
        vi.stubEnv("VITE_IS_TEST_ENVIRONMENT", "true");
        signIn("PlatformAdmin", ACME_ID);
        sessionStorage.setItem("impersonatedCompany", JSON.stringify({ id: SABOR_ID }));

        const users = await getUsers();
        expect(users.items.map(user => user.id)).toContain("usr-2");
        expect(users.items.map(user => user.id)).not.toContain("usr-1");
        await expect(updateUserRole("usr-1", "Operator")).rejects.toThrow("escopo permitido");
        await expect(updateUserRole("usr-2", "PlatformAdmin")).resolves.toBeUndefined();
    });

    it("permite ao PlatformAdmin global atuar nas empresas, mas rejeita IDs inexistentes", async () => {
        vi.stubEnv("VITE_IS_TEST_ENVIRONMENT", "true");
        signIn("PlatformAdmin", ACME_ID);

        const users = await getUsers();
        expect(users.totalItems).toBe(45);
        await expect(updateUserActiveStatus("usr-2", false)).resolves.toBeUndefined();
        await expect(updateUserActiveStatus("usr-999", false)).rejects.toThrow("escopo permitido");
    });
});
