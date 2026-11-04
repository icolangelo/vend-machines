import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import Index from "@/pages/Index";
import type { DashboardStats } from "@/lib/api";

const mocks = vi.hoisted(() => ({
    getMachines: vi.fn(),
    getLocations: vi.fn(),
    getTopProducts: vi.fn(),
    getBottomProducts: vi.fn(),
    getDashboardStats: vi.fn(),
}));

vi.mock("@/lib/api", () => ({
    getMachines: mocks.getMachines,
    getLocations: mocks.getLocations,
    getTopProducts: mocks.getTopProducts,
    getBottomProducts: mocks.getBottomProducts,
    getDashboardStats: mocks.getDashboardStats,
}));
vi.mock("@/contexts/useAuth", () => ({ useAuth: () => ({ logout: vi.fn() }) }));
vi.mock("@/components/AppSidebar", () => ({ AppSidebar: () => <nav aria-label="Navegação" /> }));
vi.mock("@/components/dashboard/MachineStatusSummary", () => ({ MachineStatusSummary: () => null }));
vi.mock("@/components/dashboard/MachineAlertTable", () => ({ MachineAlertTable: () => null }));
vi.mock("@/components/dashboard/RevenueChart", () => ({ RevenueChart: () => null }));
vi.mock("@/components/dashboard/ClientRanking", () => ({ ClientRanking: () => null }));
vi.mock("@/components/dashboard/ProductRanking", () => ({ ProductRanking: () => null }));

const makeStats = (sales: number, revenue: number): DashboardStats => ({
    totalRevenue30d: revenue,
    totalSales30d: sales,
    onlineMachines: 1,
    warningMachines: 0,
    offlineMachines: 0,
    revenueHistory: [],
    performanceHistory: [],
});

function deferred<T>() {
    let resolve!: (value: T) => void;
    const promise = new Promise<T>(done => { resolve = done; });
    return { promise, resolve };
}

function deliveredUnitsCard() {
    const title = screen.getByText("Unidades entregues 30d");
    return title.parentElement?.parentElement;
}

describe("Index dashboard", () => {
    beforeEach(() => {
        vi.clearAllMocks();
        mocks.getMachines.mockResolvedValue([{
            id: "machine-001",
            name: "Máquina de teste",
            clientName: "Empresa de teste",
            location: "Unidade de teste",
            status: "online",
            stockLevel: 60,
            inventoryConfigured: true,
            revenue30d: 1450,
            totalSales30d: 77,
            lastSync: "2026-10-25T12:00:00Z",
            serialNumber: "serial-001",
        }]);
        mocks.getLocations.mockResolvedValue([]);
        mocks.getTopProducts.mockResolvedValue([]);
        mocks.getBottomProducts.mockResolvedValue([]);
        mocks.getDashboardStats.mockResolvedValue(makeStats(77, 1450));
    });

    it("keeps the last confirmed KPIs visible while refreshing", async () => {
        const refresh = deferred<DashboardStats>();
        render(<Index />);

        expect(await screen.findByText(/^Última atualização:/)).toBeInTheDocument();
        expect(deliveredUnitsCard()).toHaveTextContent("77");
        mocks.getDashboardStats.mockReturnValueOnce(refresh.promise);

        fireEvent.click(screen.getByRole("button", { name: "Atualizar painel" }));

        expect(await screen.findByText("Atualizando painel...")).toBeInTheDocument();
        expect(deliveredUnitsCard()).toHaveTextContent("77");

        refresh.resolve(makeStats(89, 1810));
        await waitFor(() => expect(deliveredUnitsCard()).toHaveTextContent("89"));
    });

    it("keeps the last confirmed KPIs visible and reports a refresh failure", async () => {
        render(<Index />);

        expect(await screen.findByText(/^Última atualização:/)).toBeInTheDocument();
        mocks.getDashboardStats.mockRejectedValueOnce(new Error("API indisponível"));
        fireEvent.click(screen.getByRole("button", { name: "Atualizar painel" }));

        expect(await screen.findByRole("alert")).toHaveTextContent("Não foi possível atualizar o painel.");
        expect(deliveredUnitsCard()).toHaveTextContent("77");
    });
});
