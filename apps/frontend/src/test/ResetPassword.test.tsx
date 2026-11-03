import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import ResetPassword from "@/pages/ResetPassword";

const { useAuth, resetPassword, toast } = vi.hoisted(() => ({
    useAuth: vi.fn(),
    resetPassword: vi.fn(),
    toast: { error: vi.fn(), success: vi.fn() },
}));

vi.mock("@/contexts/useAuth", () => ({ useAuth }));
vi.mock("@/lib/api", () => ({ resetPassword }));
vi.mock("sonner", () => ({ toast }));

describe("ResetPassword", () => {
    const logout = vi.fn();

    beforeEach(() => {
        vi.clearAllMocks();
        resetPassword.mockResolvedValue(undefined);
        logout.mockResolvedValue(undefined);
        useAuth.mockReturnValue({ logout });
    });

    it("explains when the recovery token is missing", () => {
        render(
            <MemoryRouter initialEntries={["/reset-password"]}>
                <ResetPassword />
            </MemoryRouter>,
        );

        expect(screen.getByText(/O link não contém um token válido/)).toBeInTheDocument();
        expect(screen.queryByRole("button", { name: "Atualizar senha" })).not.toBeInTheDocument();
    });

    it("resets the password, closes the session and returns to login", async () => {
        render(
            <MemoryRouter initialEntries={["/reset-password?token=reset-token"]}>
                <Routes>
                    <Route path="/reset-password" element={<ResetPassword />} />
                    <Route path="/" element={<h1>Entrar</h1>} />
                </Routes>
            </MemoryRouter>,
        );

        fireEvent.change(screen.getByLabelText("Nova senha"), { target: { value: "senha-forte-de-teste" } });
        fireEvent.change(screen.getByLabelText("Confirme a nova senha"), { target: { value: "senha-forte-de-teste" } });
        fireEvent.click(screen.getByRole("button", { name: "Atualizar senha" }));

        await waitFor(() => expect(resetPassword).toHaveBeenCalledWith("reset-token", "senha-forte-de-teste"));
        expect(logout).toHaveBeenCalledOnce();
        expect(await screen.findByRole("heading", { name: "Entrar" })).toBeInTheDocument();
        expect(toast.success).toHaveBeenCalledWith("Senha atualizada. Entre com a nova senha.");
    });

    it("does not call the API when passwords do not match", () => {
        render(
            <MemoryRouter initialEntries={["/reset-password?token=reset-token"]}>
                <ResetPassword />
            </MemoryRouter>,
        );

        fireEvent.change(screen.getByLabelText("Nova senha"), { target: { value: "senha-forte-de-teste" } });
        fireEvent.change(screen.getByLabelText("Confirme a nova senha"), { target: { value: "outra-senha-forte" } });
        fireEvent.click(screen.getByRole("button", { name: "Atualizar senha" }));

        expect(resetPassword).not.toHaveBeenCalled();
        expect(toast.error).toHaveBeenCalledWith("As senhas não coincidem.");
    });

    it("allows retry with a Portuguese message when the network fails", async () => {
        resetPassword.mockRejectedValue(new TypeError("Failed to fetch"));
        render(<MemoryRouter initialEntries={["/reset-password?token=reset-token"]}><ResetPassword /></MemoryRouter>);
        fireEvent.change(screen.getByLabelText("Nova senha"), { target: { value: "senha-forte-de-teste" } });
        fireEvent.change(screen.getByLabelText("Confirme a nova senha"), { target: { value: "senha-forte-de-teste" } });
        fireEvent.click(screen.getByRole("button", { name: "Atualizar senha" }));

        await waitFor(() => expect(toast.error).toHaveBeenCalledWith("Não foi possível redefinir a senha. Tente novamente."));
        expect(logout).not.toHaveBeenCalled();
        expect(screen.getByRole("button", { name: "Atualizar senha" })).toBeEnabled();
    });
});
