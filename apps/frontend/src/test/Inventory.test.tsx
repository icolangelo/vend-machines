import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import Inventory from "@/pages/Inventory";

const { getInventorySlots, getInventoryMovements, getMachines, getProducts } = vi.hoisted(() => ({
    getInventorySlots: vi.fn(),
    getInventoryMovements: vi.fn(),
    getMachines: vi.fn(),
    getProducts: vi.fn(),
}));

vi.mock("@/lib/api", () => ({
    getInventorySlots,
    getInventoryMovements,
    getMachines,
    getProducts,
}));

vi.mock("@/components/AppSidebar", () => ({
    AppSidebar: () => <nav aria-label="Navegação" />,
}));

vi.mock("@/contexts/AuthContext", () => ({
    useAuth: () => ({ user: { role: "Operator" } }),
}));

vi.mock("@/contexts/ImpersonationContext", () => ({
    useImpersonation: () => ({ isSuperAdmin: false }),
}));

describe("Inventory", () => {
    beforeEach(() => {
        vi.clearAllMocks();
        getMachines.mockResolvedValue([]);
        getProducts.mockResolvedValue([]);
        getInventorySlots.mockResolvedValue([]);
        getInventoryMovements.mockResolvedValue({ items: [], page: 1, pageSize: 50, totalItems: 0, totalPages: 0 });
    });

    it("shows an unavailable state instead of an empty inventory after load failure, then retries", async () => {
        getInventorySlots
            .mockReset()
            .mockRejectedValueOnce(new Error("API indisponível"))
            .mockResolvedValueOnce([]);

        render(
            <MemoryRouter>
                <Inventory />
            </MemoryRouter>,
        );

        expect(await screen.findByRole("alert")).toHaveTextContent(
            "Não foi possível carregar o estoque. Confira sua conexão e tente novamente.",
        );
        expect(screen.queryByText("Nenhum compartimento cadastrado para esta empresa.")).not.toBeInTheDocument();
        expect(screen.queryByText("Compartimentos cadastrados")).not.toBeInTheDocument();

        fireEvent.click(screen.getByRole("button", { name: "Tentar novamente" }));
        await waitFor(() => expect(getInventorySlots).toHaveBeenCalledTimes(2));
        expect(await screen.findByText("Nenhum compartimento cadastrado para esta empresa.")).toBeInTheDocument();
        expect(screen.queryByRole("alert")).not.toBeInTheDocument();
        expect(screen.getByText("Compartimentos cadastrados")).toBeInTheDocument();
    });
});
