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

vi.mock("@/contexts/AuthContext", () => ({
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
});
