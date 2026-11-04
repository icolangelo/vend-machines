import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import Machines from "@/pages/Machines";

const api = vi.hoisted(() => ({
    getMachines: vi.fn(),
    getLocations: vi.fn(),
}));

vi.mock("@/lib/api", () => api);

vi.mock("@/components/AppSidebar", () => ({
    AppSidebar: () => <nav aria-label="Navegação" />,
}));

vi.mock("@/contexts/useAuth", () => ({
    useAuth: () => ({ user: { role: "Operator" } }),
}));

const machine = {
    id: "machine-1",
    name: "Máquina do saguão",
    clientName: "Hospital de teste",
    location: "Recepção",
    status: "online" as const,
    stockLevel: 50,
    inventoryConfigured: true,
    revenue30d: 300,
    totalSales30d: 12,
    lastSync: "agora",
    serialNumber: "serial-1",
};

describe("Machine list loading state", () => {
    beforeEach(() => {
        vi.clearAllMocks();
        api.getLocations.mockResolvedValue([]);
    });

    it("keeps the last machine list visible when refresh fails and can retry", async () => {
        api.getMachines
            .mockResolvedValueOnce([machine])
            .mockRejectedValueOnce(new Error("API indisponível"))
            .mockResolvedValueOnce([machine]);

        render(<MemoryRouter><Machines /></MemoryRouter>);
        expect(await screen.findByText("Máquina do saguão")).toBeInTheDocument();

        fireEvent.click(screen.getByRole("button", { name: "Atualizar" }));
        expect(await screen.findByRole("alert")).toHaveTextContent("Os dados exibidos podem estar desatualizados.");
        expect(screen.getByText("Máquina do saguão")).toBeInTheDocument();

        fireEvent.click(screen.getByRole("button", { name: "Tentar novamente" }));
        await waitFor(() => expect(api.getMachines).toHaveBeenCalledTimes(3));
        expect(screen.getByText("Máquina do saguão")).toBeInTheDocument();
        expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    });

    it("filters restock by the configured compartment threshold instead of a fixed percentage", async () => {
        api.getMachines.mockResolvedValue([
            {
                ...machine,
                id: "machine-below-slot-threshold",
                name: "Abaixo do limite do compartimento",
                stockLevel: 80,
                lowStock: true,
            },
            {
                ...machine,
                id: "machine-above-slot-threshold",
                name: "Acima do limite do compartimento",
                stockLevel: 10,
                lowStock: false,
            },
        ]);

        render(<MemoryRouter><Machines /></MemoryRouter>);

        expect(await screen.findByText("Abaixo do limite do compartimento")).toBeInTheDocument();
        fireEvent.mouseDown(screen.getByRole("tab", { name: "Reposição" }), { button: 0 });

        await waitFor(() => {
            expect(screen.getByText("Abaixo do limite do compartimento")).toBeInTheDocument();
            expect(screen.queryByText("Acima do limite do compartimento")).not.toBeInTheDocument();
        });
    });

    it("filters machines below their 30-day sales target and treats the exact target as met", async () => {
        api.getMachines.mockResolvedValue([
            {
                ...machine,
                id: "machine-below-sales-target",
                name: "Abaixo da meta de vendas",
                totalSales30d: 4,
                minimumSalesTarget30d: 5,
                belowSalesTarget: true,
            },
            {
                ...machine,
                id: "machine-at-sales-target",
                name: "Na meta de vendas",
                totalSales30d: 5,
                minimumSalesTarget30d: 5,
                belowSalesTarget: false,
            },
            {
                ...machine,
                id: "machine-without-sales-target",
                name: "Sem meta de vendas",
                totalSales30d: 0,
                minimumSalesTarget30d: null,
                belowSalesTarget: false,
            },
        ]);

        render(<MemoryRouter><Machines /></MemoryRouter>);

        expect(await screen.findByText("Abaixo da meta de vendas")).toBeInTheDocument();
        fireEvent.mouseDown(screen.getByRole("tab", { name: "Abaixo da meta" }), { button: 0 });

        await waitFor(() => {
            expect(screen.getByText("Abaixo da meta de vendas")).toBeInTheDocument();
            expect(screen.queryByText("Na meta de vendas")).not.toBeInTheDocument();
            expect(screen.queryByText("Sem meta de vendas")).not.toBeInTheDocument();
        });
    });
});
