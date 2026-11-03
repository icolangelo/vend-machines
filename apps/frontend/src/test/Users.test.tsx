import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import Users from "@/pages/Users";

const { getUsers, updateUserRole, toast } = vi.hoisted(() => ({
    getUsers: vi.fn(),
    updateUserRole: vi.fn(),
    toast: { error: vi.fn(), success: vi.fn() },
}));

vi.mock("@/lib/api", () => ({
    getUsers,
    createPasswordResetLink: vi.fn(),
    createUserInvitation: vi.fn(),
    revokeUserInvitation: vi.fn(),
    updateUserActiveStatus: vi.fn(),
    updateUserRole,
}));

vi.mock("@/components/AppSidebar", () => ({
    AppSidebar: () => <nav aria-label="Navegação" />,
}));

vi.mock("@/contexts/useAuth", () => ({
    useAuth: () => ({ user: { id: "admin-1", role: "Admin" } }),
}));

vi.mock("sonner", () => ({ toast }));

const userPage = {
    items: [{ id: "user-1", name: "Ana Silva", email: "ana@example.com", role: "Operator", isActive: true, createdAt: "2026-10-25T12:00:00Z" }],
    pageNumber: 1,
    pageSize: 10,
    totalItems: 1,
    totalPages: 1,
};

describe("Users", () => {
    beforeEach(() => {
        vi.clearAllMocks();
        getUsers.mockReset();
        updateUserRole.mockReset();
        getUsers.mockResolvedValue({ ...userPage, items: [] });
    });
    afterEach(() => vi.restoreAllMocks());

    it("shows an initial load error instead of an empty user list and retries", async () => {
        getUsers
            .mockReset()
            .mockRejectedValueOnce(new Error("API indisponível"))
            .mockResolvedValueOnce(userPage);

        render(<Users />);

        expect(await screen.findByRole("alert")).toHaveTextContent("Não foi possível carregar os usuários.");
        expect(screen.queryByText("Nenhum usuário encontrado.")).not.toBeInTheDocument();

        fireEvent.click(screen.getByRole("button", { name: "Tentar novamente" }));
        expect(await screen.findByText("Ana Silva")).toBeInTheDocument();
        expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    });

    it("preserves the last loaded users and marks the result stale when a new search fails", async () => {
        getUsers
            .mockReset()
            .mockResolvedValueOnce(userPage)
            .mockRejectedValueOnce(new Error("API indisponível"));

        render(<Users />);

        expect(await screen.findByText("Ana Silva")).toBeInTheDocument();
        fireEvent.change(screen.getByRole("textbox", { name: "Buscar usuários" }), { target: { value: "Bruna" } });
        fireEvent.click(screen.getByRole("button", { name: "Buscar" }));

        expect(await screen.findByRole("alert")).toHaveTextContent("Os dados exibidos podem estar desatualizados.");
        expect(screen.getByText("Ana Silva")).toBeInTheDocument();
        expect(screen.getByRole("combobox", { name: "Papel de Ana Silva" })).toBeDisabled();
        expect(screen.getByRole("button", { name: "Desativar" })).toBeDisabled();
        expect(screen.getByRole("button", { name: "Recuperar acesso" })).toBeDisabled();
        expect(screen.queryByText("Nenhum usuário encontrado.")).not.toBeInTheDocument();
    });

    it("ignores an older response when a later search has already completed", async () => {
        let resolveOld!: (value: typeof userPage) => void;
        let resolveNew!: (value: typeof userPage) => void;
        getUsers.mockImplementation((_page: number, _size: number, term: string) => new Promise(resolve => {
            if (!term) resolveOld = resolve;
            else resolveNew = resolve;
        }));
        render(<Users />);
        await waitFor(() => expect(getUsers).toHaveBeenCalledWith(1, 10, ""));
        fireEvent.change(screen.getByRole("textbox", { name: "Buscar usuários" }), { target: { value: "Bruna" } });
        fireEvent.click(screen.getByRole("button", { name: "Buscar" }));
        await waitFor(() => expect(getUsers).toHaveBeenCalledWith(1, 10, "Bruna"));
        const bruna = { ...userPage, items: [{ ...userPage.items[0], id: "user-2", name: "Bruna" }] };
        await act(async () => resolveNew(bruna));
        expect(await screen.findByText("Bruna")).toBeInTheDocument();
        await act(async () => resolveOld(userPage));
        expect(screen.getByText("Bruna")).toBeInTheDocument();
        expect(screen.queryByText("Ana Silva")).not.toBeInTheDocument();
        expect(screen.getByRole("combobox", { name: "Papel de Bruna" })).toBeEnabled();
    });

    it("does not reload an old query when a user change completes after a new search", async () => {
        let finishChange!: () => void;
        getUsers.mockResolvedValueOnce(userPage).mockResolvedValueOnce({ ...userPage, items: [{ ...userPage.items[0], id: "user-2", name: "Bruna" }] });
        updateUserRole.mockImplementation(() => new Promise<void>(resolve => { finishChange = resolve; }));
        vi.spyOn(window, "confirm").mockReturnValue(true);
        render(<Users />);
        fireEvent.change(await screen.findByRole("combobox", { name: "Papel de Ana Silva" }), { target: { value: "Admin" } });
        fireEvent.change(screen.getByRole("textbox", { name: "Buscar usuários" }), { target: { value: "Bruna" } });
        fireEvent.click(screen.getByRole("button", { name: "Buscar" }));
        expect(await screen.findByText("Bruna")).toBeInTheDocument();
        await act(async () => finishChange());
        await waitFor(() => expect(toast.success).toHaveBeenCalled());
        expect(getUsers).toHaveBeenCalledTimes(2);
        expect(screen.getByText("Bruna")).toBeInTheDocument();
    });
});
