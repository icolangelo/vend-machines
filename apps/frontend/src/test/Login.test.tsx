import { fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import Login from "@/pages/Login";

const { useAuth } = vi.hoisted(() => ({ useAuth: vi.fn() }));
vi.mock("@/contexts/AuthContext", () => ({ useAuth }));

describe("Login registration compatibility", () => {
  const register = vi.fn();
  const login = vi.fn();
  const clearSessionMessage = vi.fn();

  beforeEach(() => {
    vi.clearAllMocks();
    useAuth.mockReturnValue({ login, register, token: null, sessionMessage: null, clearSessionMessage });
  });

  async function submitRegistration() {
    fireEvent.click(screen.getByRole("button", { name: "Create an account" }));
    fireEvent.change(screen.getByLabelText("Nome Completo"), { target: { value: "Maria da Silva" } });
    fireEvent.change(screen.getByLabelText("E-mail Comercial"), { target: { value: "maria@example.com" } });
    fireEvent.change(screen.getByLabelText("CPF"), { target: { value: "52998224725" } });
    fireEvent.change(screen.getByLabelText("Senha de Acesso"), { target: { value: "senha-segura-123" } });
    fireEvent.change(screen.getByLabelText("Confirmar Senha"), { target: { value: "senha-segura-123" } });
    fireEvent.change(screen.getByLabelText("Nome da Empresa"), { target: { value: "Empresa Teste" } });
    fireEvent.change(screen.getByLabelText("CNPJ"), { target: { value: "11222333000181" } });
    fireEvent.click(screen.getByRole("checkbox"));
    fireEvent.click(screen.getByRole("button", { name: "Finalizar Cadastro" }));
  }

  it("shows pending confirmation without opening a dashboard session", async () => {
    const message = "Cadastro recebido. Confirme seu e-mail e aguarde a análise da empresa.";
    register.mockResolvedValue({ kind: "pending", status: "Pending", message });
    render(
      <MemoryRouter initialEntries={["/"]}>
        <Routes>
          <Route path="/" element={<Login />} />
          <Route path="/dashboard" element={<h1>Painel</h1>} />
        </Routes>
      </MemoryRouter>,
    );

    await submitRegistration();

    expect(await screen.findByRole("status")).toHaveTextContent(message);
    expect(screen.queryByRole("heading", { name: "Painel" })).not.toBeInTheDocument();
  });

  it("keeps the legacy flow when the API returns an authenticated session", async () => {
    register.mockResolvedValue({
      kind: "session",
      session: {
        token: "legacy-token",
        expiresAt: new Date(Date.now() + 60_000).toISOString(),
        user: { id: "user-1", name: "Maria da Silva", email: "maria@example.com", role: "Admin" },
      },
    });
    render(
      <MemoryRouter initialEntries={["/"]}>
        <Routes>
          <Route path="/" element={<Login />} />
          <Route path="/dashboard" element={<h1>Painel</h1>} />
        </Routes>
      </MemoryRouter>,
    );

    await submitRegistration();

    expect(await screen.findByRole("heading", { name: "Painel" })).toBeInTheDocument();
  });
});
