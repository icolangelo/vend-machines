import { defineConfig, devices } from "@playwright/test";

const baseURL = process.env.PLAYWRIGHT_BASE_URL;
if (!baseURL) {
    throw new Error("PLAYWRIGHT_BASE_URL deve apontar para a imagem frontend em validação.");
}

export default defineConfig({
    testDir: "./e2e",
    testMatch: "**/production-image.e2e.ts",
    fullyParallel: true,
    forbidOnly: !!process.env.CI,
    retries: process.env.CI ? 2 : 0,
    workers: process.env.CI ? 1 : undefined,
    reporter: "list",
    use: {
        baseURL,
        trace: "on-first-retry",
    },
    projects: [{
        name: "chromium",
        use: { ...devices["Desktop Chrome"] },
    }],
});
