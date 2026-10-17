import { expect, test } from "@playwright/test";

test("imagem de produção serve login, assets imutáveis, rotas SPA e CSP", async ({ page, request, baseURL }) => {
    const violations: string[] = [];
    const apiRequests: Array<{ pathname: string; method: string }> = [];
    const blockedExternalRequests: string[] = [];
    const appOrigin = new URL(baseURL!).origin;
    await page.route("**/*", async route => {
        const apiRequest = route.request();
        const requestUrl = new URL(apiRequest.url());
        if (requestUrl.origin === appOrigin) {
            await route.continue();
            return;
        }

        if (requestUrl.origin !== "https://api.vendmachine.com.br") {
            blockedExternalRequests.push(requestUrl.origin);
            await route.abort("blockedbyclient");
            return;
        }

        apiRequests.push({
            pathname: requestUrl.pathname,
            method: apiRequest.method(),
        });
        await route.fulfill({
            status: 401,
            contentType: "application/json",
            headers: {
                "Access-Control-Allow-Origin": new URL(baseURL!).origin,
                "Access-Control-Allow-Credentials": "true",
            },
            body: JSON.stringify({ message: "Sessão de teste ausente." }),
        });
    });

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
    expect(policy).not.toContain("fonts.googleapis.com");
    expect(policy).not.toContain("fonts.gstatic.com");

    const scriptPath = await page.locator("script[type='module']").getAttribute("src");
    expect(scriptPath).toMatch(/^\/assets\/.+\.js$/);
    const appBaseUrl = baseURL!;
    const script = await request.get(new URL(scriptPath!, appBaseUrl).toString());
    expect(script.status()).toBe(200);
    expect(script.headers()["cache-control"]).toContain("31536000");

    const loadedScripts = await page.evaluate(() => performance.getEntriesByType("resource")
        .map(entry => entry.name)
        .filter(url => new URL(url).pathname.startsWith("/assets/") && new URL(url).pathname.endsWith(".js")));
    const builtJavaScript: string[] = [];
    const scriptSources = new Map<string, string>();
    for (const scriptUrl of new Set(loadedScripts)) {
        const builtScript = await request.get(scriptUrl);
        expect(builtScript.status()).toBe(200);
        const source = await builtScript.text();
        builtJavaScript.push(source);
        scriptSources.set(new URL(scriptUrl).pathname, source);
    }
    const builtBundle = builtJavaScript.join("\n");
    expect(builtBundle).toContain("https://api.vendmachine.com.br/api");
    expect(builtBundle).not.toContain("VITE_IS_TEST_ENVIRONMENT");
    const authClientSource = [...scriptSources.entries()]
        .find(([pathname]) => /\/authClient-[^/]+\.js$/.test(pathname))?.[1];
    expect(authClientSource).toBeDefined();
    expect(authClientSource).toContain("https://api.vendmachine.com.br/api");
    expect(authClientSource).not.toMatch(/(?:https?:\/\/)?(?:localhost|127\.0\.0\.1)(?::\d+)?/i);

    const missingAsset = await request.get(new URL("/assets/not-a-real-bundle.js", appBaseUrl).toString());
    expect(missingAsset.status()).toBe(404);

    const deepRoute = await request.get(new URL("/machines", appBaseUrl).toString());
    expect(deepRoute.status()).toBe(200);
    expect(await deepRoute.text()).toContain("Vend Machines | Gestão de Vending Machines");

    await expect.poll(() => apiRequests).toEqual([{ pathname: "/api/auth/refresh", method: "POST" }]);
    expect(blockedExternalRequests).toEqual([]);
    expect(violations).toEqual([]);
});
