import { expect, test } from "@playwright/test";

test("imagem de produção serve login, assets imutáveis, rotas SPA e CSP", async ({ page, request, baseURL }) => {
    const violations: string[] = [];
    page.on("console", message => {
        if (message.type() === "error" && /content security policy|blocked by csp/i.test(message.text())) {
            violations.push(message.text());
        }
    });

    const response = await page.goto("/");
    expect(response?.status()).toBe(200);
    await expect(page).toHaveTitle("Vend Machines | Gestão de Vending Machines");
    await expect(page.locator("html")).toHaveAttribute("lang", "pt-BR");
    await expect(page.getByLabel("E-mail")).toBeVisible();
    await expect(page.getByLabel("Senha")).toBeVisible();

    const policy = response?.headers()["content-security-policy"];
    expect(policy).toContain("default-src 'self'");
    expect(policy).toContain("wss://api.vendmachine.com.br");

    const scriptPath = await page.locator("script[type='module']").getAttribute("src");
    expect(scriptPath).toMatch(/^\/assets\/.+\.js$/);
    const appBaseUrl = baseURL!;
    const script = await request.get(new URL(scriptPath!, appBaseUrl).toString());
    expect(script.status()).toBe(200);
    expect(script.headers()["cache-control"]).toContain("31536000");

    const missingAsset = await request.get(new URL("/assets/not-a-real-bundle.js", appBaseUrl).toString());
    expect(missingAsset.status()).toBe(404);

    const deepRoute = await request.get(new URL("/machines", appBaseUrl).toString());
    expect(deepRoute.status()).toBe(200);
    expect(await deepRoute.text()).toContain("Vend Machines | Gestão de Vending Machines");

    expect(violations).toEqual([]);
});
