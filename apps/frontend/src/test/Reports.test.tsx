import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import Reports from "@/pages/Reports";

const { getMachines, getLocations, getProducts } = vi.hoisted(() => ({
    getMachines: vi.fn(),
    getLocations: vi.fn(),
    getProducts: vi.fn(),
}));

vi.mock("@/lib/api", () => ({
    getMachines,
    getLocations,
    getProducts,
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
});
