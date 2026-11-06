import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import Alerts from "@/pages/Alerts";
import type { OperationalAlertItem } from "@/lib/api";

const mocks = vi.hoisted(() => ({
    getMachines: vi.fn(),
    getOperationalAlerts: vi.fn(),
    getOperationalAlertSummary: vi.fn(),
    markOperationalAlertRead: vi.fn(),
    resolveIncompleteSessionClosure: vi.fn(),
    userRole: "Admin" as string,
}));

vi.mock("@/lib/api", () => ({
    getMachines: mocks.getMachines,
    getOperationalAlerts: mocks.getOperationalAlerts,
    getOperationalAlertSummary: mocks.getOperationalAlertSummary,
    markOperationalAlertRead: mocks.markOperationalAlertRead,
    resolveIncompleteSessionClosure: mocks.resolveIncompleteSessionClosure,
}));
vi.mock("@/contexts/useAuth", () => ({
    useAuth: () => ({ user: { role: mocks.userRole }, logout: vi.fn() }),
}));
vi.mock("@/components/AppSidebar", () => ({ AppSidebar: () => <nav aria-label="Navegação" /> }));

function CurrentRoute() {
    const location = useLocation();
    return <output data-testid="current-route">{location.pathname}{location.search}</output>;
}

function deferred<T>() {
    let resolve!: (value: T) => void;
    const promise = new Promise<T>(done => { resolve = done; });
    return { promise, resolve };
}

function createAlert(id: string, title: string, type: "TelemetryOffline" | "TelemetryOwnerUnknown" | "LowStock" | "PaymentPending" | "SessionFailure", transactionId?: string): OperationalAlertItem {
    return {
        id,
        machineId: `machine-${id}`,
        machineName: `Máquina ${id}`,
        clientName: "Unidade de teste",
        type,
        severity: "warning" as const,
        status: "Active" as const,
        title,
        description: "Alerta de teste.",
        occurredAt: "2026-10-25T12:00:00Z",
        updatedAt: "2026-10-25T12:00:00Z",
        sessionId: undefined,
        sessionState: undefined,
        isRead: false,
        ...(transactionId ? { transactionId } : {}),
    };
}

function createPage(items: OperationalAlertItem[], page = 1, total = items.length) {
    return { items, page, pageSize: 50, total };
}

describe("Alerts", () => {
    beforeEach(() => {
        vi.clearAllMocks();
        mocks.userRole = "Admin";
        mocks.getMachines.mockResolvedValue([]);
        mocks.getOperationalAlertSummary.mockResolvedValue({ disconnectedMachines: 3, otherActiveAlerts: 4 });
        mocks.getOperationalAlerts.mockResolvedValue(createPage([{
            id: "alert-1",
            machineId: "machine 1",
            machineName: "Máquina de teste",
            clientName: "Unidade de teste",
            type: "TelemetryOwnerUnknown",
            severity: "warning",
            status: "Active",
            title: "Roteamento da máquina indisponível",
            description: "A máquina está conectada sem proprietário roteável.",
            occurredAt: "2026-10-25T12:00:00Z",
            updatedAt: "2026-10-25T12:00:00Z",
            isRead: false,
        }]));
        mocks.resolveIncompleteSessionClosure.mockResolvedValue(undefined);
    });

    it("opens telemetry for the machine from its routing alert", async () => {
        render(
            <MemoryRouter initialEntries={["/alerts"]}>
                <Routes>
                    <Route path="/alerts" element={<Alerts />} />
                    <Route path="/telemetry" element={<p>Detalhes de telemetria</p>} />
                </Routes>
                <CurrentRoute />
            </MemoryRouter>,
        );

        fireEvent.click(await screen.findByRole("button", { name: "Ver telemetria" }));

        expect(await screen.findByText("Detalhes de telemetria")).toBeInTheDocument();
        expect(screen.getByTestId("current-route")).toHaveTextContent("/telemetry?machineId=machine%201");
    });

    it("requires an idle-machine confirmation and evidence before resolving incomplete closure", async () => {
        mocks.getOperationalAlerts.mockResolvedValueOnce(createPage([{
            ...createAlert("closure", "Encerramento incompleto", "TelemetryOffline"),
            type: "SessionFailure",
            sessionId: "session-closure",
            sessionState: "ClosureIncomplete",
        }]));
        render(
            <MemoryRouter initialEntries={["/alerts"]}>
                <Routes><Route path="/alerts" element={<Alerts />} /></Routes>
            </MemoryRouter>,
        );

        fireEvent.click(await screen.findByRole("button", { name: "Confirmar máquina ociosa" }));
        expect(await screen.findByRole("heading", { name: "Confirmar encerramento físico" })).toBeInTheDocument();
        const submit = screen.getByRole("button", { name: "Registrar confirmação" });
        expect(submit).toBeDisabled();

        fireEvent.change(screen.getByLabelText("Referência do chamado ou registro"), { target: { value: "SUP-1234" } });
        fireEvent.click(screen.getByRole("checkbox"));
        expect(submit).toBeEnabled();
        fireEvent.click(submit);

        await waitFor(() => expect(mocks.resolveIncompleteSessionClosure).toHaveBeenCalledWith("session-closure", "SUP-1234"));
        expect(await screen.findByText(/Roteamento da máquina indisponível/)).toBeInTheDocument();
    });

    it("does not offer physical closure confirmation to Operator", async () => {
        mocks.userRole = "Operator";
        mocks.getOperationalAlerts.mockResolvedValueOnce(createPage([{
            ...createAlert("closure", "Encerramento incompleto", "SessionFailure"),
            sessionId: "session-closure",
            sessionState: "ClosureIncomplete",
        }]));
        render(
            <MemoryRouter initialEntries={["/alerts"]}>
                <Routes><Route path="/alerts" element={<Alerts />} /></Routes>
            </MemoryRouter>,
        );

        expect(await screen.findByText("Encerramento incompleto", { exact: false })).toBeInTheDocument();
        expect(screen.queryByRole("button", { name: "Confirmar máquina ociosa" })).not.toBeInTheDocument();
    });

    it("opens inventory filtered to the machine from a low-stock alert", async () => {
        mocks.getOperationalAlerts.mockResolvedValueOnce(createPage([
            createAlert("stock 1", "Estoque baixo", "LowStock"),
        ]));
        render(
            <MemoryRouter initialEntries={["/alerts"]}>
                <Routes>
                    <Route path="/alerts" element={<Alerts />} />
                    <Route path="/inventory" element={<p>Estoque da máquina</p>} />
                </Routes>
                <CurrentRoute />
            </MemoryRouter>,
        );

        fireEvent.click(await screen.findByRole("button", { name: "Ver estoque" }));

        expect(await screen.findByText("Estoque da máquina")).toBeInTheDocument();
        expect(screen.getByTestId("current-route")).toHaveTextContent("/inventory?machineId=machine-stock%201");
    });

    it("opens the matching payment from a pending-payment alert", async () => {
        mocks.getOperationalAlerts.mockResolvedValueOnce(createPage([
            createAlert("payment", "Pagamento pendente", "PaymentPending", "transaction 42"),
        ]));
        render(
            <MemoryRouter initialEntries={["/alerts"]}>
                <Routes>
                    <Route path="/alerts" element={<Alerts />} />
                    <Route path="/payments" element={<p>Detalhes do pagamento</p>} />
                </Routes>
                <CurrentRoute />
            </MemoryRouter>,
        );

        fireEvent.click(await screen.findByRole("button", { name: "Ver pagamento" }));

        expect(await screen.findByText("Detalhes do pagamento")).toBeInTheDocument();
        expect(screen.getByTestId("current-route")).toHaveTextContent("/payments?transactionId=transaction%2042");
    });

    it("ignores a slower response from the previously selected alert filter", async () => {
        render(
            <MemoryRouter initialEntries={["/alerts"]}>
                <Routes><Route path="/alerts" element={<Alerts />} /></Routes>
            </MemoryRouter>,
        );

        expect(await screen.findByRole("button", { name: "Ver telemetria" })).toBeInTheDocument();

        const staleResponse = deferred<ReturnType<typeof createPage>>();
        const currentResponse = deferred<ReturnType<typeof createPage>>();
        mocks.getOperationalAlerts
            .mockReturnValueOnce(staleResponse.promise)
            .mockReturnValueOnce(currentResponse.promise);

        fireEvent.click(screen.getByRole("combobox"));
        fireEvent.click(await screen.findByRole("option", { name: "Apenas desconectadas" }));
        fireEvent.click(screen.getByRole("combobox"));
        fireEvent.click(await screen.findByRole("option", { name: "Apenas Atenção" }));
        await waitFor(() => expect(mocks.getOperationalAlerts).toHaveBeenCalledTimes(3));

        currentResponse.resolve(createPage([createAlert("current", "Alerta do filtro atual", "TelemetryOwnerUnknown")]));
        expect(await screen.findByText(/Alerta do filtro atual/)).toBeInTheDocument();

        staleResponse.resolve(createPage([createAlert("stale", "Alerta do filtro antigo", "TelemetryOffline")]));
        expect(await screen.findByText(/Alerta do filtro atual/)).toBeInTheDocument();
        expect(screen.queryByText(/Alerta do filtro antigo/)).not.toBeInTheDocument();
    });

    it("keeps active summary counts independent of the selected alert history filter", async () => {
        render(
            <MemoryRouter initialEntries={["/alerts"]}>
                <Routes><Route path="/alerts" element={<Alerts />} /></Routes>
            </MemoryRouter>,
        );

        expect(await screen.findByRole("button", { name: "Ver telemetria" })).toBeInTheDocument();
        mocks.getOperationalAlerts.mockResolvedValueOnce(createPage([]));
        fireEvent.click(screen.getByRole("combobox"));
        fireEvent.click(await screen.findByRole("option", { name: "Histórico resolvido" }));
        expect(await screen.findByText("Sem alertas para exibir")).toBeInTheDocument();

        expect(screen.getByText("Máquinas desconectadas").parentElement).toHaveTextContent("3");
        expect(screen.getByText("Outros alertas ativos").parentElement).toHaveTextContent("4");
    });

    it("shows summary metrics as unavailable after the initial data load fails", async () => {
        mocks.getMachines.mockRejectedValueOnce(new Error("API indisponível"));
        mocks.getOperationalAlerts.mockRejectedValueOnce(new Error("API indisponível"));
        mocks.getOperationalAlertSummary.mockRejectedValueOnce(new Error("API indisponível"));

        render(
            <MemoryRouter initialEntries={["/alerts"]}>
                <Routes><Route path="/alerts" element={<Alerts />} /></Routes>
            </MemoryRouter>,
        );

        expect(await screen.findByRole("alert")).toHaveTextContent("Não foi possível carregar os alertas");
        expect(screen.getAllByText("Indisponível")).toHaveLength(3);
    });

    it("keeps the last summary values visible when a refresh fails", async () => {
        render(
            <MemoryRouter initialEntries={["/alerts"]}>
                <Routes><Route path="/alerts" element={<Alerts />} /></Routes>
            </MemoryRouter>,
        );

        expect(await screen.findByRole("button", { name: "Ver telemetria" })).toBeInTheDocument();
        mocks.getMachines.mockRejectedValueOnce(new Error("API indisponível"));
        mocks.getOperationalAlerts.mockRejectedValueOnce(new Error("API indisponível"));
        mocks.getOperationalAlertSummary.mockRejectedValueOnce(new Error("API indisponível"));

        fireEvent.click(screen.getByRole("button", { name: "Atualizar" }));

        expect(await screen.findByRole("alert")).toHaveTextContent("Alguns dados de alertas não puderam ser atualizados");
        expect(screen.getByText("Máquinas desconectadas").parentElement).toHaveTextContent("3");
        expect(screen.getByText("Outros alertas ativos").parentElement).toHaveTextContent("4");
    });

    it("keeps the alert list available when only its summary request fails", async () => {
        mocks.getOperationalAlertSummary.mockRejectedValueOnce(new Error("API indisponível"));

        render(
            <MemoryRouter initialEntries={["/alerts"]}>
                <Routes><Route path="/alerts" element={<Alerts />} /></Routes>
            </MemoryRouter>,
        );

        expect(await screen.findByRole("button", { name: "Ver telemetria" })).toBeInTheDocument();
        expect(await screen.findByRole("alert")).toHaveTextContent("Alguns dados de alertas não puderam ser atualizados");
        expect(screen.getAllByText("Indisponível")).toHaveLength(2);
    });

    it("loads the next server page and displays its range", async () => {
        const firstPage = Array.from({ length: 50 }, (_, index) => createAlert(`first-${index}`, `Alerta inicial ${index}`, "TelemetryOwnerUnknown"));
        mocks.getOperationalAlerts.mockResolvedValueOnce(createPage(firstPage, 1, 51));
        mocks.getOperationalAlerts.mockResolvedValueOnce(createPage([
            createAlert("last", "Alerta da página seguinte", "TelemetryOwnerUnknown"),
        ], 2, 51));

        render(
            <MemoryRouter initialEntries={["/alerts"]}>
                <Routes><Route path="/alerts" element={<Alerts />} /></Routes>
            </MemoryRouter>,
        );

        expect(await screen.findByText("Exibindo 1–50 de 51 alertas")).toBeInTheDocument();
        fireEvent.click(screen.getByRole("button", { name: "Próxima" }));

        expect(await screen.findByText(/Alerta da página seguinte/)).toBeInTheDocument();
        expect(screen.getByText("Exibindo 51–51 de 51 alertas")).toBeInTheDocument();
        expect(mocks.getOperationalAlerts).toHaveBeenLastCalledWith("All", 2, 50, "All", "");
    });
});
