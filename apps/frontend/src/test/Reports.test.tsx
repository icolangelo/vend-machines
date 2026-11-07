import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import Reports from "@/pages/Reports";

const { getMachines, getLocations, getProducts, getInventoryMovements, getSalesReport, exportSalesReport, exportInventoryMovements } = vi.hoisted(() => ({
    getMachines: vi.fn(),
    getLocations: vi.fn(),
    getProducts: vi.fn(),
    getInventoryMovements: vi.fn(),
    getSalesReport: vi.fn(),
    exportSalesReport: vi.fn(),
    exportInventoryMovements: vi.fn(),
}));
const companyContext = vi.hoisted(() => ({ id: "company-a" }));
vi.mock("@/contexts/useAuth", () => ({ useAuth: () => ({ user: { id: "user-a", companyId: null } }) }));
vi.mock("@/contexts/useImpersonation", () => ({ useImpersonation: () => ({ impersonatedCompany: { id: companyContext.id } }) }));

vi.mock("@/lib/api", () => ({
    getMachines,
    getLocations,
    getProducts,
    getInventoryMovements,
    getSalesReport,
    exportSalesReport,
    exportInventoryMovements,
}));

vi.mock("@/components/AppSidebar", () => ({
    AppSidebar: () => <nav aria-label="Navegação" />,
}));

describe("Reports", () => {
    beforeEach(() => {
        companyContext.id = "company-a";
        vi.clearAllMocks();
        getMachines.mockResolvedValue([]);
        getLocations.mockResolvedValue([]);
        getProducts.mockResolvedValue([]);
        getInventoryMovements.mockResolvedValue({ items: [], page: 1, pageSize: 50, totalItems: 0, totalPages: 0 });
        getSalesReport.mockResolvedValue({ from: "2026-10-01", to: "2026-10-24", total: 0, truncated: false, items: [] });
        exportInventoryMovements.mockResolvedValue(new Blob(["csv"]));
        vi.stubGlobal("URL", {
            ...URL,
            createObjectURL: vi.fn(() => "blob:inventory-export"),
            revokeObjectURL: vi.fn(),
        });
    });

    it("clears results and discards a pending export after changing the company", async () => {
        let finishExport!: (value: Blob) => void;
        exportSalesReport.mockReset().mockImplementationOnce(() => new Promise(resolve => { finishExport = resolve; }));
        const { rerender } = render(<Reports />);
        fireEvent.click(screen.getByRole("combobox"));
        fireEvent.click(await screen.findByRole("option", { name: "Vendas e Receita" }));
        fireEvent.click(screen.getByRole("button", { name: "Executar" }));
        fireEvent.click(await screen.findByRole("button", { name: "Exportar CSV completo" }));
        expect(exportSalesReport).toHaveBeenCalledTimes(1);
        companyContext.id = "company-b";
        rerender(<Reports />);
        expect(screen.queryByRole("button", { name: "Exportar CSV completo" })).not.toBeInTheDocument();
        await act(async () => finishExport(new Blob(["dados da empresa anterior"])));
        expect(URL.createObjectURL).not.toHaveBeenCalled();
    });

    it("discards a pending report when the company changes", async () => {
        let finish!: (value: unknown) => void;
        getSalesReport.mockReset().mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
        const { rerender } = render(<Reports />);
        fireEvent.click(screen.getByRole("combobox"));
        fireEvent.click(await screen.findByRole("option", { name: "Vendas e Receita" }));
        fireEvent.click(screen.getByRole("button", { name: "Executar" }));
        companyContext.id = "company-b";
        rerender(<Reports />);
        await act(async () => finish({ from: "2026-10-01", to: "2026-10-24", total: 0, truncated: false, items: [] }));
        expect(screen.queryByRole("button", { name: "Exportar CSV completo" })).not.toBeInTheDocument();
        expect(screen.getByText("Nenhum relatório executado")).toBeInTheDocument();
    });

    it("blocks inventory export and pagination until changed filters are executed", async () => {
        getInventoryMovements.mockReset().mockResolvedValue({ items: [], page: 1, pageSize: 50, totalItems: 51, totalPages: 2 });
        render(<Reports />);
        fireEvent.click(screen.getByRole("combobox"));
        fireEvent.click(await screen.findByRole("option", { name: "Movimentações de Estoque" }));
        fireEvent.click(screen.getByRole("button", { name: "Executar" }));
        expect(await screen.findByRole("button", { name: "Próxima" })).toBeEnabled();
        fireEvent.change(screen.getByLabelText("Compartimento"), { target: { value: "2" } });
        expect(screen.getByRole("status")).toHaveTextContent("Execute novamente");
        expect(screen.getByRole("button", { name: "Exportar CSV completo" })).toBeDisabled();
        expect(screen.getByRole("button", { name: "Próxima" })).toBeDisabled();
        fireEvent.click(screen.getByRole("button", { name: "Executar" }));
        await waitFor(() => expect(getInventoryMovements).toHaveBeenLastCalledWith(1, 50, { machineId: undefined, itemNumber: 2 }));
        await waitFor(() => expect(screen.getByRole("button", { name: "Exportar CSV completo" })).toBeEnabled());
    });

    it("does not enable sales export for an old response after the date changes", async () => {
        let finish!: (value: unknown) => void;
        getSalesReport.mockReset().mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
        render(<Reports />);
        fireEvent.click(screen.getByRole("combobox"));
        fireEvent.click(await screen.findByRole("option", { name: "Vendas e Receita" }));
        fireEvent.click(screen.getByRole("button", { name: "Executar" }));
        fireEvent.change(screen.getByLabelText("De"), { target: { value: "2026-01-01" } });
        await act(async () => finish({ from: "2026-10-01", to: "2026-10-24", total: 0, truncated: false, items: [] }));
        expect(await screen.findByRole("button", { name: "Exportar CSV completo" })).toBeDisabled();
        expect(screen.getByRole("button", { name: "Imprimir" })).toBeDisabled();
        expect(screen.getByRole("status")).toHaveTextContent("Execute novamente");
        expect(exportSalesReport).not.toHaveBeenCalled();
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
        expect(screen.getByRole("button", { name: "Exportar CSV completo" })).toBeInTheDocument();
        fireEvent.click(screen.getByRole("button", { name: "Exportar CSV completo" }));
        await waitFor(() => expect(exportInventoryMovements).toHaveBeenCalledWith({ machineId: undefined, itemNumber: undefined }));
        fireEvent.click(screen.getByRole("button", { name: "Próxima" }));

        expect(await screen.findByRole("alert")).toHaveTextContent("Não foi possível carregar esta página do histórico.");
        fireEvent.click(screen.getByRole("button", { name: "Tentar novamente" }));
        await waitFor(() => expect(getInventoryMovements).toHaveBeenLastCalledWith(2, 50, { machineId: undefined, itemNumber: undefined }));
        expect(await screen.findByText("Página 2 de 2")).toBeInTheDocument();
        expect(screen.getByText("Exibindo 51–51 de 51 movimentações · atualizado em", { exact: false })).toBeInTheDocument();
        expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    });

    it.each([
        ["ReconciliationRequired", "Pendente de conciliação"],
        ["Disputed", "Em contestação"],
    ])("marks %s sales as unconfirmed instead of showing revenue", async (financialStatus, revenueLabel) => {
        getSalesReport.mockResolvedValue({
            from: "2026-10-01",
            to: "2026-10-24",
            total: 1,
            truncated: false,
            items: [{
                id: "sale-1",
                soldAt: "2026-10-20T12:00:00Z",
                machineId: "machine-1",
                machineName: "Máquina A",
                clientName: "Cliente A",
                location: "Unidade A",
                itemNumber: 2,
                productName: "Produto A",
                quantity: 1,
                grossAmount: 100,
                refundedAmount: 0,
                netRevenue: null,
                applicationFee: 5,
                netAfterPlatformFee: 95,
                financialStatus,
            }],
        });

        render(<Reports />);
        fireEvent.click(screen.getByRole("combobox"));
        fireEvent.click(await screen.findByRole("option", { name: "Vendas e Receita" }));
        fireEvent.click(screen.getByRole("button", { name: "Executar" }));

        await waitFor(() => expect(screen.getAllByText(revenueLabel)).toHaveLength(financialStatus === "Disputed" ? 2 : 1));
        if (financialStatus === "ReconciliationRequired")
            expect(screen.getByText("Conciliação necessária")).toBeInTheDocument();
        await waitFor(() => expect(getSalesReport).toHaveBeenCalledWith(
            expect.any(String),
            expect.any(String),
            { machineId: undefined, locationId: undefined, productId: undefined },
        ));
    });
});
