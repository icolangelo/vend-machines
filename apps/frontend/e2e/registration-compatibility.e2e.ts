import { expect, test } from "@playwright/test";

test("reports a pending registration without opening an authenticated session", async ({ page }) => {
  const confirmation = "Cadastro recebido. Confirme seu e-mail e aguarde a análise da empresa.";
  await page.route("**/auth/refresh", route => route.fulfill({ status: 401, json: { message: "Sessão ausente." } }));
  await page.route("**/auth/register", route => route.fulfill({
    status: 202,
    json: { status: "Pending", verificationEmailSent: true, message: confirmation },
  }));
  await page.addInitScript(() => localStorage.setItem("vm-cookies-consent", JSON.stringify({ essential: true, analytics: false, marketing: false })));

  await page.goto("/#/");
  await page.getByRole("button", { name: "Create an account" }).click();
  await page.getByLabel("Nome Completo").fill("Maria da Silva");
  await page.getByLabel("E-mail Comercial").fill("maria@example.com");
  await page.getByLabel("CPF").fill("52998224725");
  await page.getByLabel("Senha de Acesso").fill("senha-segura-123");
  await page.getByLabel("Confirmar Senha").fill("senha-segura-123");
  await page.getByLabel("Nome da Empresa").fill("Empresa Teste");
  await page.getByLabel("CNPJ").fill("11222333000181");
  await page.locator("#agreedToPolicy").check();
  await page.getByRole("button", { name: "Finalizar Cadastro" }).click();

  await expect(page.getByRole("status")).toHaveText(confirmation);
  await expect(page).toHaveURL(/#\/$/);
  await expect(page.getByRole("heading", { name: "Painel" })).toHaveCount(0);
});
