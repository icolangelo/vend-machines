import { expect, test, type Page } from "@playwright/test";

async function signIn(page: Page, email: string, password: string) {
  await page.addInitScript(() => {
    localStorage.setItem("vm-cookies-consent", JSON.stringify({ essential: true, analytics: false, marketing: false }));
  });
  await page.goto("/");
  await expect(page.getByText("Ambiente de Teste", { exact: true })).toBeVisible();
  await page.getByLabel("E-mail").fill(email);
  await page.getByLabel("Senha").fill(password);
  await page.getByRole("button", { name: "Entrar", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Painel", exact: true })).toBeVisible();
}

async function navigateMobile(page: Page, linkName: string, headingName: string) {
  await page.getByRole("button", { name: "Alternar menu lateral" }).first().click();
  await page.getByRole("link", { name: linkName, exact: true }).click();
  await expect(page.getByRole("heading", { name: headingName, exact: true, level: 1 })).toBeVisible();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
}

test("PlatformAdmin vê as ações administrativas na navegação", async ({ page }) => {
  await page.goto("/");
  await expect(page).toHaveTitle("Vend Machines | Gestão de Vending Machines");
  await expect(page.locator("html")).toHaveAttribute("lang", "pt-BR");
  await expect(page.getByLabel("E-mail")).toBeVisible();
  await expect(page.getByLabel("Senha")).toBeVisible();
  await expect(page.getByRole("button", { name: "Log in with Google" })).toHaveCount(0);
  await signIn(page, "admin@example.com", "admin123");

  await expect(page.getByRole("link", { name: "Admin", exact: true })).toBeVisible();
  await page.getByRole("link", { name: "Máquinas", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Máquinas", exact: true })).toBeVisible();
  await page.getByRole("tab", { name: "Indisponíveis ou atenção" }).click();
  await expect(page.getByText("VM-005", { exact: true })).toBeVisible();
  await expect(page.getByText("VM-001", { exact: true })).toHaveCount(0);
  await page.getByRole("tab", { name: "Reposição" }).click();
  await expect(page.getByText("VM-002", { exact: true })).toBeVisible();
  await expect(page.getByText("VM-001", { exact: true })).toHaveCount(0);
  await page.getByRole("tab", { name: "Abaixo da meta" }).click();
  await expect(page.getByText("VM-002", { exact: true })).toBeVisible();
  await expect(page.getByText("VM-001", { exact: true })).toHaveCount(0);
  await page.getByRole("tab", { name: "Todas as Máquinas" }).click();
  await expect(page.getByRole("button", { name: "Nova Máquina" })).toBeVisible();
  await page.getByRole("button", { name: "Nova Máquina" }).click();
  await expect(page.getByText("Número de série *")).toBeVisible();
  await expect(page.getByRole("tab", { name: "Equipamento" })).toBeVisible();
  await expect(page.getByRole("tab", { name: "Telemetria" })).toBeVisible();
});

test("Painel mostra o faturamento dos clientes no período de trinta dias", async ({ page }) => {
  await signIn(page, "admin@acme.example", "admin123");

  const ranking = page.getByRole("region", { name: "Faturamento por cliente" });
  await expect(ranking).toBeVisible();
  await expect(ranking.getByText("Após estornos e antes da taxa da plataforma")).toBeVisible();
  await expect(ranking.getByText("ACME - Shopping", { exact: true })).toBeVisible();
  await expect(ranking.getByText("30 dias", { exact: true })).toBeVisible();
});

test("Operator navega no celular sem ações administrativas nem rolagem horizontal", async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 812 });
  await signIn(page, "operador@example.com", "teste123");

  await expect(page.getByRole("link", { name: "Admin", exact: true })).toHaveCount(0);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);

  await navigateMobile(page, "Máquinas", "Máquinas");
  await expect(page.getByText("VM-001", { exact: true })).toBeVisible();
  await expect(page.getByText("VM-101", { exact: true })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Nova Máquina" })).toHaveCount(0);
  await navigateMobile(page, "Alertas", "Alertas e Notificações");
  await expect(page.getByRole("heading", { name: "Sem alertas para exibir" })).toBeVisible();
  await navigateMobile(page, "Relatórios", "Relatórios");
  await expect(page.getByRole("heading", { name: "Nenhum relatório executado" })).toBeVisible();
  await page.getByRole("button", { name: "Executar", exact: true }).click();
  await expect(page.getByText("VM-001", { exact: true })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await navigateMobile(page, "Estoque", "Estoque");
  await expect(page.getByText("Nenhum compartimento cadastrado para esta empresa.")).toBeVisible();
});

test("Operator da Sabor & Cia vê somente os dados da própria empresa no celular", async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 812 });
  await signIn(page, "operador@sabor.example", "teste123");

  await expect(page.getByRole("link", { name: "Admin", exact: true })).toHaveCount(0);
  await expect(page.getByRole("link", { name: "Usuários", exact: true })).toHaveCount(0);

  await navigateMobile(page, "Máquinas", "Máquinas");
  await expect(page.getByText("VM-101", { exact: true })).toBeVisible();
  await expect(page.getByText("VM-102", { exact: true })).toBeVisible();
  await expect(page.getByText("VM-001", { exact: true })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Nova Máquina" })).toHaveCount(0);
  await page.getByRole("tab", { name: "Indisponíveis ou atenção" }).click();
  await expect(page.getByText("VM-102", { exact: true })).toBeVisible();
  await expect(page.getByText("VM-101", { exact: true })).toHaveCount(0);
  await page.getByRole("tab", { name: "Reposição" }).click();
  await expect(page.getByText("VM-102", { exact: true })).toBeVisible();
  await page.getByRole("tab", { name: "Abaixo da meta" }).click();
  await expect(page.getByText("VM-102", { exact: true })).toBeVisible();
  await page.getByRole("tab", { name: "Todas as Máquinas" }).click();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);

  await navigateMobile(page, "Localizações", "Localizações");
  await expect(page.getByText("Sabor & Cia - Centro", { exact: true })).toBeVisible();
  await expect(page.getByText("ACME - Hospital", { exact: true })).toHaveCount(0);
});

test("Admin da Sabor & Cia gerencia usuários somente da própria empresa", async ({ page }) => {
  await signIn(page, "admin@sabor.example", "admin123");
  await expect(page.getByRole("link", { name: "Usuários", exact: true })).toBeVisible();
  await expect(page.getByRole("link", { name: "Admin", exact: true })).toHaveCount(0);
  await page.getByRole("link", { name: "Usuários", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Usuários", exact: true })).toBeVisible();
  await expect(page.getByText("João da Silva", { exact: true })).toBeVisible();
  await expect(page.getByText("Admin Ivan", { exact: true })).toHaveCount(0);

  const userRow = page.getByRole("row").filter({ hasText: "João da Silva" });
  page.once("dialog", (dialog) => dialog.accept());
  await userRow.getByLabel("Papel de João da Silva").selectOption("Admin");
  await expect(userRow.getByLabel("Papel de João da Silva")).toHaveValue("Admin");

  page.once("dialog", (dialog) => dialog.accept());
  await userRow.getByRole("button", { name: "Desativar" }).click();
  await expect(userRow.getByRole("button", { name: "Reativar" })).toBeVisible();
  page.once("dialog", (dialog) => dialog.accept());
  await userRow.getByRole("button", { name: "Reativar" }).click();
  await expect(userRow.getByRole("button", { name: "Desativar" })).toBeVisible();

  await page.getByRole("button", { name: "Convidar usuário" }).click();
  const invitationDialog = page.getByRole("dialog");
  await invitationDialog.getByLabel("E-mail").fill("novo.operador@sabor.example");
  await invitationDialog.getByRole("button", { name: "Gerar convite", exact: true }).click();
  await expect(invitationDialog.getByLabel("Link de convite")).toHaveValue(/mock-invitation-/);
  page.once("dialog", (dialog) => dialog.accept());
  await invitationDialog.getByRole("button", { name: "Revogar convite" }).click();
  await expect(invitationDialog.getByLabel("Link de convite")).toHaveCount(0);
  await page.keyboard.press("Escape");
  await expect(invitationDialog).toHaveCount(0);

  page.once("dialog", (dialog) => dialog.accept());
  await userRow.getByRole("button", { name: "Recuperar acesso" }).click();
  await expect(page.getByLabel("Link de recuperação")).toHaveValue(/mock-reset-/);
});

test("Admin da ACME vê somente os usuários da própria empresa", async ({ page }) => {
  await signIn(page, "admin@acme.example", "admin123");
  await expect(page.getByRole("link", { name: "Usuários", exact: true })).toBeVisible();
  await page.getByRole("link", { name: "Usuários", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Usuários", exact: true })).toBeVisible();
  await expect(page.getByText("Admin Ivan", { exact: true })).toBeVisible();
  await expect(page.getByText("João da Silva", { exact: true })).toHaveCount(0);
  await expect(page.getByText("Sabor & Cia Vending", { exact: true })).toHaveCount(0);
});

test("PlatformAdmin pode trocar de empresa e retornar os dados ao sair da visualização", async ({ page }) => {
  await signIn(page, "admin@example.com", "admin123");
  const companyBadge = page.getByTitle("Duplo clique para trocar empresa");
  await companyBadge.click();
  await companyBadge.click();

  await expect(page.getByText("Visualizar como empresa", { exact: true })).toBeVisible();
  await page.getByText("Sabor & Cia Vending", { exact: true }).click();
  await expect(page.getByTitle("Duplo clique para trocar empresa")).toContainText("Sabor & Cia Vending");
  await page.getByRole("link", { name: "Máquinas", exact: true }).click();
  await expect(page.getByText("VM-101", { exact: true })).toBeVisible();
  await expect(page.getByText("VM-001", { exact: true })).toHaveCount(0);

  const switchedBadge = page.getByTitle("Duplo clique para trocar empresa");
  await switchedBadge.click();
  await switchedBadge.click();
  await page.getByRole("button", { name: /Sair/ }).click();
  await page.getByRole("link", { name: "Máquinas", exact: true }).click();
  await expect(page.getByText("VM-001", { exact: true })).toBeVisible();
  await expect(page.getByText("VM-101", { exact: true })).toHaveCount(0);
});
