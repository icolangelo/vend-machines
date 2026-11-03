import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { MemoryRouter } from "react-router-dom";
import Telemetry from "@/pages/Telemetry";

const state = vi.hoisted(() => ({
    role: "Operator",
    toast: vi.fn(),
    getTelemetryConnections: vi.fn(),
    getTelemetryEvents: vi.fn(),
    createTelemetrySocketTicket: vi.fn(),
    setTelemetryMonitoring: vi.fn(),
    startTelemetrySession: vi.fn(),
    cancelTelemetrySession: vi.fn(),
    requestMdbStatus: vi.fn(),
    sendTelemetryCommand: vi.fn(),
}));

vi.mock("@/lib/api", () => ({
    API_BASE_URL: "http://localhost/api",
    getTelemetryConnections: state.getTelemetryConnections,
    getTelemetryEvents: state.getTelemetryEvents,
    createTelemetrySocketTicket: state.createTelemetrySocketTicket,
    setTelemetryMonitoring: state.setTelemetryMonitoring,
    startTelemetrySession: state.startTelemetrySession,
    cancelTelemetrySession: state.cancelTelemetrySession,
    requestMdbStatus: state.requestMdbStatus,
    sendTelemetryCommand: state.sendTelemetryCommand,
}));

vi.mock("@/components/AppSidebar", () => ({ AppSidebar: () => <nav aria-label="Navegação" /> }));
vi.mock("@/contexts/useAuth", () => ({ useAuth: () => ({ user: { role: state.role } }) }));
vi.mock("@/components/ui/use-toast", () => ({ useToast: () => ({ toast: state.toast }) }));

const connection = {
    machineId: "machine-1",
    machineName: "Máquina de teste",
    serialNumber: "SN-100",
    location: "Unidade de teste",
    online: true,
    commandRouteAvailable: true,
    monitoringEnabled: true,
    autoOpenSessionEnabled: false,
    manualStartRequiresMdb: false,
    mdbStatus: "enabled_state",
    mdbStatusFresh: true,
    activeSession: {
        sessionId: "session-1",
        state: "PaymentPending",
        startedAt: "2026-10-24T12:00:00Z",
    },
};

const secondConnection = {
    ...connection,
    machineId: "machine-2",
    machineName: "Segunda máquina",
    serialNumber: "SN-200",
};

describe("Telemetry permissions", () => {
    beforeEach(() => {
        vi.clearAllMocks();
        state.getTelemetryConnections.mockResolvedValue([connection]);
        state.getTelemetryEvents.mockResolvedValue([]);
        state.createTelemetrySocketTicket.mockImplementation(() => new Promise(() => {}));
    });

    afterEach(() => cleanup());

    it("permite ao Operator consultar, mas bloqueia ações e comandos MDB", async () => {
        state.role = "Operator";
        render(<MemoryRouter><Telemetry /></MemoryRouter>);

        expect(await screen.findByRole("status")).toHaveTextContent("Modo somente leitura");
        expect(screen.getByRole("button", { name: "Desativar acompanhamento" })).toBeDisabled();
        expect(screen.getByRole("button", { name: "Consultar MDB" })).toBeDisabled();
        expect(screen.getByRole("button", { name: "Cancelar venda" })).toBeDisabled();
        expect(screen.getByRole("button", { name: "RELATORIO AUDITORIA" })).toBeDisabled();
        expect(screen.getByRole("button", { name: "REQUISICAO CONFIGURACAO" })).toBeDisabled();
        expect(screen.getByRole("button", { name: "ATUALIZAR CONFIGURACAO" })).toBeDisabled();
    });

    it("permite ao Admin operar a telemetria da máquina selecionada", async () => {
        state.role = "Admin";
        render(<MemoryRouter><Telemetry /></MemoryRouter>);

        expect(await screen.findByRole("button", { name: "Desativar acompanhamento" })).toBeEnabled();
        expect(screen.getByRole("button", { name: "Consultar MDB" })).toBeEnabled();
        expect(screen.getByRole("button", { name: "Cancelar venda" })).toBeEnabled();
        expect(screen.getByRole("button", { name: "RELATORIO AUDITORIA" })).toBeEnabled();
    });

    it("shows a connection loading error instead of an empty machine list and retries", async () => {
        state.getTelemetryConnections
            .mockReset()
            .mockRejectedValueOnce(new Error("API indisponível"))
            .mockResolvedValueOnce([connection]);
        state.role = "Operator";
        render(<MemoryRouter><Telemetry /></MemoryRouter>);

        expect(await screen.findByRole("alert")).toHaveTextContent("Não foi possível carregar as conexões das máquinas.");
        expect(screen.queryByText("Nenhuma máquina cadastrada.")).not.toBeInTheDocument();
        expect(screen.getByText("Máquinas").parentElement).toHaveTextContent("—");

        fireEvent.click(screen.getByRole("button", { name: "Tentar novamente" }));
        expect(await screen.findByRole("button", { name: "Consultar MDB" })).toBeDisabled();
        expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    });

    it("shows event loading errors and retries without labeling the failure as an empty history", async () => {
        state.getTelemetryEvents
            .mockReset()
            .mockRejectedValueOnce(new Error("API indisponível"))
            .mockResolvedValueOnce([]);
        render(<MemoryRouter><Telemetry /></MemoryRouter>);

        expect(await screen.findByRole("alert")).toHaveTextContent("Não foi possível carregar os eventos desta máquina.");
        expect(screen.queryByText("Nenhum evento registrado.")).not.toBeInTheDocument();

        fireEvent.click(screen.getByRole("button", { name: "Tentar novamente" }));
        expect(await screen.findByText("Nenhum evento registrado.")).toBeInTheDocument();
        expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    });

    it("keeps the last machine event visible when refreshing the history fails", async () => {
        const event = { id: "event-1", eventType: "device.connected", createdAt: "2026-10-25T12:00:00Z" };
        state.getTelemetryEvents
            .mockReset()
            .mockResolvedValueOnce([event])
            .mockRejectedValueOnce(new Error("API indisponível"));
        render(<MemoryRouter><Telemetry /></MemoryRouter>);

        expect(await screen.findByText("Máquina conectada")).toBeInTheDocument();
        fireEvent.click(screen.getByRole("button", { name: "Atualizar eventos" }));

        expect(await screen.findByRole("alert")).toHaveTextContent("A lista exibida pode estar desatualizada.");
        expect(screen.getByText("Máquina conectada")).toBeInTheDocument();
        await waitFor(() => expect(state.getTelemetryEvents).toHaveBeenCalledTimes(2));
    });

    it("ignores a delayed event response after selecting another machine", async () => {
        let resolveFirstMachineEvents!: (events: Array<{ id: string; eventType: string; createdAt: string }>) => void;
        state.getTelemetryConnections.mockResolvedValue([connection, secondConnection]);
        state.getTelemetryEvents.mockImplementation((machineId: string) => machineId === "machine-1"
            ? new Promise(resolve => { resolveFirstMachineEvents = resolve; })
            : Promise.resolve([{ id: "event-2", eventType: "device.disconnected", createdAt: "2026-10-25T12:01:00Z" }]));

        render(<MemoryRouter><Telemetry /></MemoryRouter>);

        fireEvent.click(await screen.findByRole("button", { name: /Segunda máquina/ }));
        expect(await screen.findByText("Máquina desconectada")).toBeInTheDocument();

        await act(async () => {
            resolveFirstMachineEvents([{ id: "event-1", eventType: "device.connected", createdAt: "2026-10-25T12:00:00Z" }]);
        });
        expect(screen.queryByText("Máquina conectada")).not.toBeInTheDocument();
        expect(screen.getByText("Máquina desconectada")).toBeInTheDocument();
    });

    it("mantém a conexão visível mas bloqueia ações quando a rota da máquina não é compatível", async () => {
        state.role = "Admin";
        state.getTelemetryConnections.mockResolvedValue([{ ...connection, commandRouteAvailable: false }]);
        render(<MemoryRouter><Telemetry /></MemoryRouter>);

        expect(await screen.findByRole("alert")).toHaveTextContent("A conexão física está ativa");
        expect(screen.getAllByText("Aguardando reconexão").length).toBeGreaterThan(0);
        expect(screen.getByRole("button", { name: "Consultar MDB" })).toBeDisabled();
        expect(screen.getByRole("button", { name: "Cancelar venda" })).toBeDisabled();
        expect(screen.getByRole("button", { name: "RELATORIO AUDITORIA" })).toBeDisabled();
    });

    it("permite solicitar o cancelamento financeiro de Pix pendente quando a máquina está offline", async () => {
        state.role = "Admin";
        state.getTelemetryConnections.mockResolvedValue([{
            ...connection,
            online: false,
            commandRouteAvailable: false,
        }]);
        render(<MemoryRouter><Telemetry /></MemoryRouter>);

        expect(await screen.findByRole("button", { name: "Cancelar venda" })).toBeEnabled();
    });

    it("seleciona a máquina indicada no link do alerta", async () => {
        state.role = "Admin";
        state.getTelemetryConnections.mockResolvedValue([
            connection,
            { ...connection, machineId: "machine-2", machineName: "Máquina indicada pelo alerta" },
        ]);
        render(
            <MemoryRouter initialEntries={["/telemetry?machineId=machine-2"]}>
                <Telemetry />
            </MemoryRouter>,
        );

        expect(await screen.findByRole("button", { name: /Máquina indicada pelo alerta/ })).toHaveAttribute("aria-pressed", "true");
    });

    it("moves selection to an available machine when the selected machine disappears", async () => {
        state.getTelemetryConnections
            .mockResolvedValueOnce([connection])
            .mockResolvedValueOnce([secondConnection]);
        render(<MemoryRouter><Telemetry /></MemoryRouter>);

        expect(await screen.findByRole("button", { name: /Máquina de teste/ })).toHaveAttribute("aria-pressed", "true");
        fireEvent.click(screen.getByRole("button", { name: "Atualizar conexões" }));

        expect(await screen.findByRole("button", { name: /Segunda máquina/ })).toHaveAttribute("aria-pressed", "true");
        expect(screen.getByText("Segunda máquina", { selector: "h3" })).toBeInTheDocument();
    });
});
