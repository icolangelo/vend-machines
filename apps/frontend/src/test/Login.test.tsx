import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import Login from "@/pages/Login";

const { useAuth } = vi.hoisted(() => ({ useAuth: vi.fn() }));
vi.mock("@/contexts/useAuth", () => ({ useAuth }));

describe("Login", () => {
    const login = vi.fn();
    const register = vi.fn();
    const clearSessionMessage = vi.fn();

    beforeEach(() => {
        vi.clearAllMocks();
        login.mockResolvedValue(undefined);
        register.mockResolvedValue("Cadastro recebido. Aguarde a confirmação do e-mail.");
        useAuth.mockReturnValue({ login, register, token: null, sessionMessage: null, clearSessionMessage });
    });

    it("shows authentication failures and keeps the user on the login page", async () => {
        login.mockRejectedValue(new Error("Credenciais inválidas."));
        render(
            <MemoryRouter initialEntries={["/"]}>
                <Routes>
                    <Route path="/" element={<Login />} />
                    <Route path="/dashboard" element={<h1>Painel</h1>} />
                </Routes>
            </MemoryRouter>,
        );

        fireEvent.change(screen.getByLabelText("E-mail"), { target: { value: "operador@example.com" } });
        fireEvent.change(screen.getByLabelText("Senha"), { target: { value: "senha-errada" } });
        fireEvent.click(screen.getByRole("button", { name: "Entrar" }));

        expect(await screen.findByText("Credenciais inválidas.")).toBeInTheDocument();
        expect(screen.queryByRole("heading", { name: "Painel" })).not.toBeInTheDocument();
    });

    it("shows a Portuguese message and permits retry when the network is unavailable", async () => {
        login.mockRejectedValue(new TypeError("Failed to fetch"));
        render(<MemoryRouter><Login /></MemoryRouter>);
        fireEvent.change(screen.getByLabelText("E-mail"), { target: { value: "operador@example.com" } });
        fireEvent.change(screen.getByLabelText("Senha"), { target: { value: "senha-de-teste" } });
        fireEvent.click(screen.getByRole("button", { name: "Entrar" }));

        expect(await screen.findByText("Ocorreu um erro ao tentar entrar. Tente novamente.")).toBeInTheDocument();
        expect(screen.getByRole("button", { name: "Entrar" })).toBeEnabled();
        expect(screen.queryByText("Failed to fetch")).not.toBeInTheDocument();
    });

    it("shows an expired-session message and clears the one-time notice", async () => {
        useAuth.mockReturnValue({
            login,
            register,
            token: null,
            sessionMessage: "Sua sessão expirou. Entre novamente.",
            clearSessionMessage,
        });
        render(
            <MemoryRouter>
                <Login />
            </MemoryRouter>,
        );

        expect(await screen.findByText("Sua sessão expirou. Entre novamente.")).toBeInTheDocument();
        await waitFor(() => expect(clearSessionMessage).toHaveBeenCalledOnce());
    });

    it("requires privacy consent before sending a company registration", async () => {
        render(
            <MemoryRouter>
                <Login />
            </MemoryRouter>,
        );

        fireEvent.click(screen.getByRole("button", { name: "Criar uma conta" }));
        fireEvent.change(screen.getByLabelText("Nome Completo"), { target: { value: "Maria da Silva" } });
        fireEvent.change(screen.getByLabelText("E-mail Comercial"), { target: { value: "maria@example.com" } });
        fireEvent.change(screen.getByLabelText("CPF"), { target: { value: "12345678901" } });
        fireEvent.change(screen.getByLabelText("Senha de Acesso"), { target: { value: "senha-forte-de-teste" } });
        fireEvent.change(screen.getByLabelText("Confirmar Senha"), { target: { value: "senha-forte-de-teste" } });
        fireEvent.change(screen.getByLabelText("Nome da Empresa"), { target: { value: "Empresa Teste" } });
        fireEvent.change(screen.getByLabelText("CNPJ"), { target: { value: "11222333000181" } });
        fireEvent.click(screen.getByRole("button", { name: "Enviar cadastro para análise" }));

        expect(register).not.toHaveBeenCalled();
        expect(await screen.findByText("Você precisa ler e concordar com a Política de Privacidade para continuar.")).toBeInTheDocument();
    });
});
