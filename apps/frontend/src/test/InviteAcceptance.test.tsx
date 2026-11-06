import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import InviteAcceptance from "@/pages/InviteAcceptance";

const { useAuth, toast } = vi.hoisted(() => ({
    useAuth: vi.fn(),
    toast: { error: vi.fn(), success: vi.fn() },
}));

vi.mock("@/contexts/useAuth", () => ({ useAuth }));
vi.mock("sonner", () => ({ toast }));

describe("InviteAcceptance", () => {
    const acceptInvitation = vi.fn();

    beforeEach(() => {
        vi.clearAllMocks();
        acceptInvitation.mockResolvedValue(undefined);
        useAuth.mockReturnValue({ acceptInvitation });
    });

    it("explains when the invitation token is missing", () => {
        render(
            <MemoryRouter initialEntries={["/invite"]}>
                <InviteAcceptance />
            </MemoryRouter>,
        );

        expect(screen.getByText(/O link não contém um convite válido/)).toBeInTheDocument();
        expect(screen.queryByRole("button", { name: "Aceitar convite e criar conta" })).not.toBeInTheDocument();
    });

    it("submits the accepted privacy policy and navigates after invitation succeeds", async () => {
        render(
            <MemoryRouter initialEntries={["/invite?token=invitation-token"]}>
                <Routes>
                    <Route path="/invite" element={<InviteAcceptance />} />
                    <Route path="/dashboard" element={<h1>Painel da conta</h1>} />
                </Routes>
            </MemoryRouter>,
        );

        fireEvent.change(screen.getByLabelText("Nome completo"), { target: { value: "Maria da Silva" } });
        fireEvent.change(screen.getByLabelText("CPF"), { target: { value: "12345678901" } });
        fireEvent.change(screen.getByLabelText("Crie uma senha"), { target: { value: "senha-forte-de-teste" } });
        fireEvent.change(screen.getByLabelText("Confirme a senha"), { target: { value: "senha-forte-de-teste" } });
        fireEvent.click(screen.getByRole("checkbox"));
        fireEvent.click(screen.getByRole("button", { name: "Aceitar convite e criar conta" }));

        await waitFor(() => expect(acceptInvitation).toHaveBeenCalledWith({
            invitationToken: "invitation-token",
            name: "Maria da Silva",
            cpf: "12345678901",
            password: "senha-forte-de-teste",
            acceptedPrivacyPolicy: true,
        }));
        expect(await screen.findByRole("heading", { name: "Painel da conta" })).toBeInTheDocument();
        expect(toast.success).toHaveBeenCalledWith("Convite aceito. Sua conta está pronta.");
    });

    it("does not send mismatched passwords to the API", () => {
        render(
            <MemoryRouter initialEntries={["/invite?token=invitation-token"]}>
                <InviteAcceptance />
            </MemoryRouter>,
        );

        fireEvent.change(screen.getByLabelText("Nome completo"), { target: { value: "Maria da Silva" } });
        fireEvent.change(screen.getByLabelText("CPF"), { target: { value: "12345678901" } });
        fireEvent.change(screen.getByLabelText("Crie uma senha"), { target: { value: "senha-forte-de-teste" } });
        fireEvent.change(screen.getByLabelText("Confirme a senha"), { target: { value: "outra-senha-forte" } });
        fireEvent.click(screen.getByRole("checkbox"));
        fireEvent.click(screen.getByRole("button", { name: "Aceitar convite e criar conta" }));

        expect(acceptInvitation).not.toHaveBeenCalled();
        expect(toast.error).toHaveBeenCalledWith("As senhas não coincidem.");
    });

    it("allows retry with a Portuguese message when invitation submission loses the network", async () => {
        acceptInvitation.mockRejectedValue(new TypeError("Failed to fetch"));
        render(<MemoryRouter initialEntries={["/invite?token=invitation-token"]}><InviteAcceptance /></MemoryRouter>);
        fireEvent.change(screen.getByLabelText("Nome completo"), { target: { value: "Maria da Silva" } });
        fireEvent.change(screen.getByLabelText("CPF"), { target: { value: "12345678901" } });
        fireEvent.change(screen.getByLabelText("Crie uma senha"), { target: { value: "senha-forte-de-teste" } });
        fireEvent.change(screen.getByLabelText("Confirme a senha"), { target: { value: "senha-forte-de-teste" } });
        fireEvent.click(screen.getByRole("checkbox"));
        fireEvent.click(screen.getByRole("button", { name: "Aceitar convite e criar conta" }));

        await waitFor(() => expect(toast.error).toHaveBeenCalledWith("Não foi possível aceitar o convite. Tente novamente."));
        expect(screen.getByRole("button", { name: "Aceitar convite e criar conta" })).toBeEnabled();
    });
});
