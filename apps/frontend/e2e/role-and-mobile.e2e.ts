import { expect, test, type Page } from "@playwright/test";

async function signIn(page: Page, email: string, password: string) {
  await page.addInitScript(() => {
    localStorage.setItem("vm-cookies-notice-v2", "read");
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
  await page.getByPlaceholder("Ex: SN-123456").fill("");
  await page.getByPlaceholder("Ex: Modelo X").fill("");
  await page.getByRole("button", { name: "Salvar Equipamento" }).first().click();
  await expect(page.getByText("Número de série é obrigatório")).toBeVisible();
  await expect(page.getByText("Modelo é obrigatório")).toBeVisible();
});

test("Login e logout são sincronizados entre abas abertas", async ({ browser }) => {
  const context = await browser.newContext();
  const firstTab = await context.newPage();
  const secondTab = await context.newPage();
  await Promise.all([firstTab.goto("/"), secondTab.goto("/")]);
  await expect(secondTab.getByRole("heading", { name: "Entrar na sua conta" })).toBeVisible();

  await signIn(firstTab, "admin@acme.example", "admin123");
  await expect(secondTab.getByRole("heading", { name: "Painel", exact: true })).toBeVisible();

  await firstTab.getByTitle("Sair").click();
  await expect(firstTab.getByRole("heading", { name: "Entrar na sua conta" })).toBeVisible();
  await expect(secondTab.getByRole("heading", { name: "Entrar na sua conta" })).toBeVisible();
  await context.close();
});

test("Painel mostra o faturamento dos clientes no período de trinta dias", async ({ page }) => {
  const browserErrors: string[] = [];
  page.on("console", message => {
    if (message.type() === "error") browserErrors.push(message.text());
  });
  await signIn(page, "admin@acme.example", "admin123");

  const ranking = page.getByRole("region", { name: "Faturamento por cliente" });
  await expect(ranking).toBeVisible();
  await expect(ranking.getByText("Após estornos, pré-taxa; faturamento pendente de conciliação excluído")).toBeVisible();
  await expect(ranking.getByText("ACME - Shopping", { exact: true })).toBeVisible();
  await expect(ranking.getByText("30 dias", { exact: true })).toBeVisible();
  expect(browserErrors).toEqual([]);
});

test("Admin gera e exporta o relatório de vendas", async ({ page }) => {
  await signIn(page, "admin@acme.example", "admin123");
  await page.getByRole("link", { name: "Relatórios", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Relatórios", exact: true })).toBeVisible();
  await page.getByRole("combobox").first().click();
  await page.getByRole("option", { name: "Vendas e Receita" }).click();
  await page.getByRole("button", { name: "Executar", exact: true }).click();

  await expect(page.getByRole("heading", { name: "Relatório de vendas e receita" })).toBeVisible();
  await expect(page.getByText("Não há vendas registradas no período selecionado.")).toBeVisible();
  const downloadPromise = page.waitForEvent("download");
  await page.getByRole("button", { name: "Exportar CSV completo", exact: true }).click();
  const download = await downloadPromise;
  expect(download.suggestedFilename()).toMatch(/^vendas-\d{4}-\d{2}-\d{2}-\d{4}-\d{2}-\d{2}\.csv$/);
});

test("Admin confirma encerramento físico e consulta a referência no histórico", async ({ page }) => {
  await signIn(page, "admin@acme.example", "admin123");
  await page.getByRole("link", { name: "Alertas", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Alertas e Notificações", exact: true })).toBeVisible();
  await expect(page.getByText("Encerramento físico pendente", { exact: false })).toBeVisible();

  await page.getByRole("button", { name: "Confirmar máquina ociosa" }).click();
  await expect(page.getByRole("heading", { name: "Confirmar encerramento físico" })).toBeVisible();
  const submit = page.getByRole("button", { name: "Registrar confirmação" });
  await expect(submit).toBeDisabled();
  await page.getByLabel("Referência do chamado ou registro").fill("SUP-2026-001");
  await page.getByRole("checkbox").check();
  await expect(submit).toBeEnabled();
  await submit.click();

  await expect(page.getByRole("button", { name: "Confirmar máquina ociosa" })).toHaveCount(0);
  await expect(page.getByText("Resolvido", { exact: true })).toBeVisible();
  await expect(page.getByText(/Referência de evidência: SUP-2026-001/)).toBeVisible();
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
  await navigateMobile(page, "Produtos", "Produtos");
  await expect(page.getByRole("button", { name: "Novo Produto", exact: true })).toHaveCount(0);
  await navigateMobile(page, "Configurações", "Configurações");
  await expect(page.getByRole("button", { name: "Novo Tipo", exact: true })).toHaveCount(0);
  await expect(page.getByRole("link", { name: "Integrações", exact: true })).toHaveCount(0);
  await navigateMobile(page, "Telemetria (MDB)", "Conexões e sessões MDB");
  await expect(page.getByText("Selecione uma máquina.")).toBeVisible();
});

test("Operator não recebe controles de alteração nos módulos administrativos", async ({ page }) => {
  await signIn(page, "operador@example.com", "teste123");

  await page.getByRole("link", { name: "Produtos", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Produtos", exact: true, level: 1 })).toBeVisible();
  await expect(page.getByRole("button", { name: "Novo Produto", exact: true })).toHaveCount(0);

  await page.getByRole("link", { name: "Localizações", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Localizações", exact: true, level: 1 })).toBeVisible();
  await expect(page.getByRole("button", { name: "Nova Localização", exact: true })).toHaveCount(0);

  await page.getByRole("link", { name: "Configurações", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Configurações", exact: true, level: 1 })).toBeVisible();
  await expect(page.getByRole("button", { name: "Novo Tipo", exact: true })).toHaveCount(0);

  await page.evaluate(() => { window.location.hash = "/integrations"; });
  await expect(page.getByRole("heading", { name: "Painel", exact: true })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Integrações", exact: true })).toHaveCount(0);

  await page.getByRole("link", { name: "Estoque", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Estoque", exact: true, level: 1 })).toBeVisible();
  await expect(page.getByRole("button", { name: "Cadastrar compartimento", exact: true })).toHaveCount(0);
});

test("Operator é redirecionado ao tentar abrir rotas administrativas diretamente", async ({ page }) => {
  await signIn(page, "operador@example.com", "teste123");

  await page.evaluate(() => { window.location.hash = "/admin"; });
  await expect(page.getByRole("heading", { name: "Painel", exact: true })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Painel de Controle Administrativo" })).toHaveCount(0);

  await page.evaluate(() => { window.location.hash = "/users"; });
  await expect(page.getByRole("heading", { name: "Painel", exact: true })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Usuários", exact: true })).toHaveCount(0);
});

test("Admin da empresa recebe controles de gestão sem acesso à administração da plataforma", async ({ page }) => {
  await signIn(page, "admin@acme.example", "admin123");
  await expect(page.getByRole("link", { name: "Usuários", exact: true })).toBeVisible();
  await expect(page.getByRole("link", { name: "Admin", exact: true })).toHaveCount(0);

  await page.getByRole("link", { name: "Produtos", exact: true }).click();
  await expect(page.getByRole("button", { name: "Novo Produto", exact: true })).toBeVisible();

  await page.getByRole("link", { name: "Localizações", exact: true }).click();
  await expect(page.getByRole("button", { name: "Nova Localização", exact: true })).toBeVisible();

  await page.getByRole("link", { name: "Configurações", exact: true }).click();
  await expect(page.getByRole("button", { name: "Novo Tipo", exact: true })).toBeVisible();

  await page.getByRole("link", { name: "Integrações", exact: true }).click();
  await expect(page.getByRole("button", { name: "Conectar com Mercado Pago", exact: true })).toBeVisible();

  await page.getByRole("link", { name: "Estoque", exact: true }).click();
  await expect(page.getByRole("button", { name: "Cadastrar compartimento", exact: true })).toBeVisible();
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
  await companyBadge.dblclick();

  await expect(page.getByText("Visualizar como empresa", { exact: true })).toBeVisible();
  await page.getByText("Sabor & Cia Vending", { exact: true }).click();
  await expect(page.getByTitle("Duplo clique para trocar empresa")).toContainText("Sabor & Cia Vending");
  await page.getByRole("link", { name: "Máquinas", exact: true }).click();
  await expect(page.getByText("VM-101", { exact: true })).toBeVisible();
  await expect(page.getByText("VM-001", { exact: true })).toHaveCount(0);

  const switchedBadge = page.getByTitle("Duplo clique para trocar empresa");
  await switchedBadge.dblclick();
  await page.getByRole("button", { name: /Sair/ }).click();
  await page.getByRole("link", { name: "Máquinas", exact: true }).click();
  await expect(page.getByText("VM-001", { exact: true })).toBeVisible();
  await expect(page.getByText("VM-101", { exact: true })).toHaveCount(0);
});

test("Relatórios são limpos ao trocar a empresa visualizada", async ({ page }) => {
  await signIn(page, "admin@example.com", "admin123");
  await page.getByTitle("Duplo clique para trocar empresa").dblclick();
  await page.getByText("Sabor & Cia Vending", { exact: true }).click();
  await expect(page.getByTitle("Duplo clique para trocar empresa")).toContainText("Sabor & Cia Vending");
  await page.getByRole("link", { name: "Relatórios", exact: true }).click();
  await page.getByRole("button", { name: "Executar", exact: true }).click();
  await expect(page.getByText("VM-101", { exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Exportar CSV", exact: true })).toBeEnabled();

  await page.getByTitle("Duplo clique para trocar empresa").dblclick();
  await page.getByRole("button", { name: /Sair/ }).click();
  await expect(page.getByRole("heading", { name: "Relatórios", exact: true })).toBeVisible();
  await expect(page.getByText("Nenhum relatório executado")).toBeVisible();
  await expect(page.getByText("VM-101", { exact: true })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Exportar CSV", exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "Executar", exact: true }).click();
  await expect(page.getByText("VM-001", { exact: true })).toBeVisible();
  await expect(page.getByText("VM-101", { exact: true })).toHaveCount(0);
});

test("Convite válido cria a sessão somente depois do aceite da política", async ({ page }) => {
  await page.goto("/#/invite?token=e2e-invitation-token");

  await expect(page.getByRole("heading", { name: "Aceitar convite" })).toBeVisible();
  await page.getByLabel("Nome completo").fill("Maria da Silva");
  await page.getByLabel("CPF").fill("12345678901");
  await page.getByLabel("Crie uma senha").fill("senha-forte-de-teste");
  await page.getByLabel("Confirme a senha").fill("senha-forte-de-teste");
  await page.getByRole("checkbox").check();
  await page.getByRole("button", { name: "Aceitar convite e criar conta" }).click();

  await expect(page.getByRole("heading", { name: "Painel", exact: true })).toBeVisible();
});

test("Recuperação de senha exige confirmação e retorna ao login", async ({ page }) => {
  await page.goto("/#/reset-password?token=e2e-reset-token");

  await expect(page.getByRole("heading", { name: "Recuperar acesso" })).toBeVisible();
  await page.getByLabel("Nova senha", { exact: true }).fill("senha-forte-de-teste");
  await page.getByLabel("Confirme a nova senha", { exact: true }).fill("senha-diferente");
  await page.getByRole("button", { name: "Atualizar senha" }).click();
  await expect(page.getByText("As senhas não coincidem.")).toBeVisible();

  await page.getByLabel("Confirme a nova senha", { exact: true }).fill("senha-forte-de-teste");
  await page.getByRole("button", { name: "Atualizar senha" }).click();
  await expect(page.getByRole("heading", { name: "Entrar na sua conta" })).toBeVisible();
});

test("Confirmação e reenvio de e-mail usam mensagens genéricas", async ({ page }) => {
  await page.goto("/#/verify-email?token=e2e-verification-token");

  await expect(page.getByRole("heading", { name: "Confirme seu e-mail" })).toBeVisible();
  await page.getByRole("button", { name: "Confirmar meu e-mail" }).click();
  await expect(page.getByRole("status")).toContainText("E-mail confirmado.");

  await page.goto("/#/verify-email");
  await page.getByLabel("E-mail do cadastro").fill("pessoa@example.com");
  await page.getByRole("button", { name: "Solicitar novo link" }).click();
  await expect(page.getByText(/Se houver um cadastro aguardando confirmação/)).toBeVisible();
});

test("Conexão simulada do Mercado Pago conclui o callback sem expor segredo", async ({ page }) => {
  await signIn(page, "admin@acme.example", "admin123");
  await page.getByRole("link", { name: "Integrações", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Integrações", exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Conectar com Mercado Pago", exact: true }).click();

  await expect(page.getByRole("button", { name: "Desconectar Conta", exact: true })).toBeVisible({ timeout: 10000 });
  await expect(page.getByText("Admin Ivan (Mock)", { exact: true })).toBeVisible();
  await expect(page.getByText("MOCKSECRET", { exact: true })).toHaveCount(0);
  expect(new URL(page.url()).search).toBe("");

  await page.getByTitle("Sair").click();
  await expect(page.getByRole("heading", { name: "Entrar na sua conta", exact: true })).toBeVisible();
  expect(await page.evaluate(() => sessionStorage.getItem("vm-test-auth-session"))).toBeNull();
});

test("Cadastro de empresa exige consentimento e funciona no celular", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByRole("complementary", { name: "Aviso sobre cookies e armazenamento" })).toBeVisible();
  await page.getByRole("button", { name: "Entendi" }).click();
  await page.getByRole("button", { name: "Criar uma conta" }).click();

  await expect(page.getByRole("heading", { name: "Criar uma nova conta" })).toBeVisible();
  await page.getByLabel("Nome Completo").fill("Maria da Silva");
  await page.getByLabel("E-mail Comercial").fill("maria@example.com");
  await page.getByLabel("CPF").fill("12345678901");
  await page.getByLabel("Senha de Acesso").fill("senha-forte-de-teste");
  await page.getByLabel("Confirmar Senha").fill("senha-forte-de-teste");
  await page.getByLabel("Nome da Empresa").fill("Empresa Teste Ltda");
  await page.getByLabel("CNPJ").fill("11222333000181");

  await page.getByRole("button", { name: "Política de Privacidade" }).click();
  await expect(page.getByRole("heading", { name: "Política de Privacidade" })).toBeVisible();
  await page.getByRole("dialog").getByRole("button", { name: "Fechar" }).first().click();
  await page.getByRole("checkbox").check();
  await page.getByRole("button", { name: "Enviar cadastro para análise" }).click();

  await expect(page.getByRole("heading", { name: "Entrar na sua conta" })).toBeVisible();
  await expect(page.getByRole("status")).toContainText("A empresa aguarda análise da equipe da plataforma.");
  await expect(page.getByRole("button", { name: "Solicitar novo link de confirmação" })).toBeVisible();

  await page.setViewportSize({ width: 375, height: 812 });
  await page.getByRole("button", { name: "Criar uma conta" }).click();
  await expect(page.getByRole("heading", { name: "Criar uma nova conta" })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);

  const submitButton = page.getByRole("button", { name: "Enviar cadastro para análise" });
  await submitButton.scrollIntoViewIfNeeded();
  await expect(submitButton).toBeVisible();
  await page.getByRole("button", { name: "Política de Privacidade" }).click();
  const privacyDialog = page.getByRole("dialog");
  await expect(privacyDialog).toBeVisible();
  expect(await privacyDialog.evaluate((element) => element.scrollWidth <= window.innerWidth)).toBe(true);
});
