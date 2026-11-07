import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const { authenticatedFetch } = vi.hoisted(() => ({ authenticatedFetch: vi.fn() }));
vi.mock("@/lib/authClient", () => ({
    API_BASE_URL: "https://api.example.test/api",
    authenticatedFetch,
    getCurrentAuthUser: vi.fn(),
    SessionExpiredError: class extends Error {},
}));

import { updateGlobalSettings } from "@/lib/api";

describe("configuração global de taxa", () => {
    beforeEach(() => {
        vi.stubEnv("VITE_IS_TEST_ENVIRONMENT", "false");
        authenticatedFetch.mockReset();
    });
    afterEach(() => vi.unstubAllEnvs());

    it.each([
        [{ applicationFeePercent: 5, isConfigured: true }, "5"],
        [{ applicationFeePercent: 5, isConfigured: false }, "missing"],
    ])("envia a condição lida para a API", async (expected, header) => {
        authenticatedFetch.mockResolvedValue(new Response(null, { status: 200 }));
        await updateGlobalSettings({ applicationFeePercent: 7 }, expected);
        expect(authenticatedFetch).toHaveBeenCalledWith("https://api.example.test/api/payments/global-settings", {
            method: "POST",
            headers: { "Content-Type": "application/json", "X-Expected-Platform-Fee": header },
            body: JSON.stringify({ applicationFeePercent: 7 }),
        });
    });

    it("não apresenta um conflito como sucesso", async () => {
        authenticatedFetch.mockResolvedValue(new Response(null, { status: 409 }));
        await expect(updateGlobalSettings({ applicationFeePercent: 7 }, { applicationFeePercent: 5, isConfigured: true })).rejects.toThrow();
    });
});
