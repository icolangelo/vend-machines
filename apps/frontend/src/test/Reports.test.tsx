import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import Reports from "@/pages/Reports";

const { getMachines, getLocations, getProducts, getInventoryMovements } = vi.hoisted(() => ({
    getMachines: vi.fn(),
    getLocations: vi.fn(),
    getProducts: vi.fn(),
    getInventoryMovements: vi.fn(),
}));

vi.mock("@/lib/api", () => ({
    getMachines,
    getLocations,
    getProducts,
    getInventoryMovements,
}));

vi.mock("@/components/AppSidebar", () => ({
    AppSidebar: () => <nav aria-label="Navegação" />,
}));

describe("Reports", () => {
    beforeEach(() => {
        vi.clearAllMocks();
        getMachines.mockResolvedValue([]);
        getLocations.mockResolvedValue([]);
        getProducts.mockResolvedValue([]);
        getInventoryMovements.mockResolvedValue({ items: [], page: 1, pageSize: 50, totalItems: 0, totalPages: 0 });
    });

    it("keeps a report error visible and retries the selected report", async () => {
        getMachines
            .mockReset()
            .mockResolvedValueOnce([])
            .mockRejectedValueOnce(new Error("API indisponível"))
            .mockResolvedValueOnce([]);

        render(<Reports />);
        await waitFor(() => expect(getMachines).toHaveBeenCalledTimes(1));
        fireEvent.click(screen.getByRole("button", { name: "Executar" }));

        expect(await screen.findByRole("alert")).toHaveTextContent(
            "Não foi possível gerar o relatório. Confira a conexão e tente novamente.",
        );
        expect(screen.queryByText("Nenhum relatório executado")).not.toBeInTheDocument();

        fireEvent.click(screen.getByRole("button", { name: "Tentar novamente" }));
        await waitFor(() => expect(getMachines).toHaveBeenCalledTimes(3));
        expect(await screen.findByText("Relatório de Máquinas e Estoque")).toBeInTheDocument();
        expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    });

    it("paginates the complete inventory history and labels the current range", async () => {
        const movement = (id: string) => ({
            id,
            machineId: "machine-1",
            itemNumber: 1,
            productId: "product-1",
            productName: "Água",
            movementType: "Restock",
            quantityDelta: 10,
            quantityAfter: 10,
            reason: "Reposição",
            createdAt: "2026-10-17T12:00:00Z",
        });
        getInventoryMovements
            .mockResolvedValueOnce({ items: [movement("movement-1")], page: 1, pageSize: 50, totalItems: 51, totalPages: 2 })
            .mockRejectedValueOnce(new Error("API indisponível"))
            .mockResolvedValueOnce({ items: [movement("movement-51")], page: 2, pageSize: 50, totalItems: 51, totalPages: 2 });

        render(<Reports />);
        fireEvent.click(screen.getByRole("combobox"));
        fireEvent.click(await screen.findByRole("option", { name: "Movimentações de Estoque" }));
        fireEvent.click(screen.getByRole("button", { name: "Executar" }));

        expect(await screen.findByText("Exibindo 1–1 de 51 movimentações · atualizado em", { exact: false })).toBeInTheDocument();
        expect(screen.getByText("Água")).toBeInTheDocument();
        expect(screen.getAllByText("Reposição")).toHaveLength(2);
        expect(screen.getByRole("button", { name: "Exportar página CSV" })).toBeInTheDocument();
        fireEvent.click(screen.getByRole("button", { name: "Próxima" }));

        expect(await screen.findByRole("alert")).toHaveTextContent("Não foi possível carregar esta página do histórico.");
        fireEvent.click(screen.getByRole("button", { name: "Tentar novamente" }));
        await waitFor(() => expect(getInventoryMovements).toHaveBeenLastCalledWith(2, 50, { machineId: undefined, itemNumber: undefined }));
        expect(await screen.findByText("Página 2 de 2")).toBeInTheDocument();
        expect(screen.getByText("Exibindo 51–51 de 51 movimentações · atualizado em", { exact: false })).toBeInTheDocument();
        expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    });
});
