import { fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import MachineFormPage from "@/pages/MachineFormPage";

const { getMachine, getLocations, getIntegration, getTelemetryConnections, toast } = vi.hoisted(() => ({
    getMachine: vi.fn(),
    getLocations: vi.fn(),
    getIntegration: vi.fn(),
    getTelemetryConnections: vi.fn(),
    toast: { error: vi.fn(), success: vi.fn() },
}));

vi.mock("@/lib/api", () => ({
    getMachine,
    getLocations,
    getIntegration,
    getTelemetryConnections,
    createMachine: vi.fn(),
    updateMachine: vi.fn(),
    requestMdbStatus: vi.fn(),
    startTelemetrySession: vi.fn(),
    rotateTelemetryCredential: vi.fn(),
    revokeTelemetryCredential: vi.fn(),
}));

vi.mock("@/components/AppSidebar", () => ({
    AppSidebar: () => <nav aria-label="Navegação" />,
}));

vi.mock("@/contexts/useAuth", () => ({
    useAuth: () => ({ user: { role: "Admin" } }),
}));

vi.mock("sonner", () => ({ toast }));

const machine = {
    id: "machine-1",
    name: "Máquina principal",
    locationId: "location-1",
    clientName: "Unidade central",
    location: "Recepção",
    status: "online" as const,
    stockLevel: 50,
    revenue30d: 1000,
    totalSales30d: 100,
    lastSync: "agora",
    serialNumber: "SN-100",
};

const locations = [{
    id: "location-1",
    name: "Unidade central",
    machineCount: 1,
    revenue30d: 1000,
    totalSales30d: 100,
}];

function renderMachineForm(path: string) {
    return render(
        <MemoryRouter initialEntries={[path]}>
            <Routes>
                <Route path="/machines/new" element={<MachineFormPage />} />
                <Route path="/machines/:id/edit" element={<MachineFormPage />} />
                <Route path="/locations" element={<h1>Localizações</h1>} />
                <Route path="/machines" element={<h1>Máquinas</h1>} />
            </Routes>
        </MemoryRouter>,
    );
}

describe("MachineFormPage data loading", () => {
    beforeEach(() => {
        vi.clearAllMocks();
        vi.stubGlobal("ResizeObserver", class {
            observe() {}
            unobserve() {}
            disconnect() {}
        });
        getMachine.mockResolvedValue(machine);
        getLocations.mockResolvedValue(locations);
        getIntegration.mockResolvedValue({ isActive: true });
        getTelemetryConnections.mockResolvedValue([]);
    });

    it("blocks editing when machine details fail to load and allows retry", async () => {
        getMachine
            .mockRejectedValueOnce(new Error("API indisponível"))
            .mockResolvedValueOnce(machine);

        renderMachineForm("/machines/machine-1/edit");

        expect(await screen.findByRole("alert")).toHaveTextContent("O formulário de edição está bloqueado");
        expect(screen.queryByPlaceholderText("Ex: SN-123456")).not.toBeInTheDocument();
        expect(screen.getByRole("button", { name: "Salvar Equipamento" })).toBeDisabled();

        fireEvent.click(screen.getByRole("button", { name: "Tentar novamente" }));
        expect(await screen.findByPlaceholderText("Ex: SN-123456")).toHaveValue("SN-100");
        expect(screen.getByRole("button", { name: "Salvar Equipamento" })).toBeEnabled();
    });

    it("keeps saving disabled when locations fail to load and recovers after retry", async () => {
        getLocations
            .mockRejectedValueOnce(new Error("API indisponível"))
            .mockResolvedValueOnce(locations);

        renderMachineForm("/machines/new");

        expect(await screen.findByRole("alert")).toHaveTextContent("não poderá ser salva até que uma localização válida seja carregada");
        expect(screen.getByRole("button", { name: "Salvar Equipamento" })).toBeDisabled();

        fireEvent.click(screen.getByRole("button", { name: "Tentar novamente" }));
        expect(await screen.findByRole("button", { name: "Salvar Equipamento" })).toBeEnabled();
    });

    it("does not report the payment integration as inactive when its status cannot be loaded", async () => {
        getIntegration.mockRejectedValueOnce(new Error("API indisponível"));

        renderMachineForm("/machines/new");

        expect(await screen.findByRole("alert")).toHaveTextContent("Não foi possível confirmar se a integração do Mercado Pago está ativa.");
        expect(screen.queryByText(/A integração com o Mercado Pago está inativa/)).not.toBeInTheDocument();
    });

    it("distinguishes an MDB query failure from a machine without an active telemetry connection", async () => {
        getTelemetryConnections
            .mockRejectedValueOnce(new Error("API indisponível"))
            .mockResolvedValueOnce([]);

        renderMachineForm("/machines/machine-1/edit");

        await screen.findByPlaceholderText("Ex: SN-123456");
        const telemetryTab = await screen.findByRole("tab", { name: "Telemetria" });
        fireEvent.mouseDown(telemetryTab, { button: 0 });
        expect(telemetryTab).toHaveAttribute("aria-selected", "true");
        expect(await screen.findByRole("alert")).toHaveTextContent("o estado da máquina é desconhecido");
        expect(screen.queryByText("Status MDB desconhecido")).not.toBeInTheDocument();

        fireEvent.click(screen.getByRole("button", { name: "Tentar novamente" }));
        expect(await screen.findByText("Máquina sem conexão de telemetria")).toBeInTheDocument();
        expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    });
});
