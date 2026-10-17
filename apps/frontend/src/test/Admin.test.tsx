import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import Admin from "@/pages/Admin";

const api = vi.hoisted(() => ({
    getUsers: vi.fn(),
    updateUserRole: vi.fn(),
    getCompanies: vi.fn(),
    updateCompanyActivation: vi.fn(),
    getGlobalSettings: vi.fn(),
    updateGlobalSettings: vi.fn(),
    getCompanyIntegration: vi.fn(),
    getAdminLogs: vi.fn(),
    getDeviceMessageInbox: vi.fn(),
    getReconciliationQueue: vi.fn(),
    getWorkerHealth: vi.fn(),
    retryDeviceMessageInbox: vi.fn(),
}));

vi.mock("@/lib/api", () => api);

vi.mock("@/components/AppSidebar", () => ({
    AppSidebar: () => <nav aria-label="Navegação" />,
}));

vi.mock("@/contexts/AuthContext", () => ({
    useAuth: () => ({ user: { role: "PlatformAdmin" }, logout: vi.fn() }),
}));

const emptyReconciliation = {
    counts: { pending: 0, reconciliationRequired: 0 },
    items: [],
    page: 1,
    pageSize: 20,
    totalItems: 0,
    totalPages: 0,
};

const workerHealthSummary = {
    generatedAt: "2026-09-28T12:00:00.000Z",
    workers: [{
        name: "telemetry-inbox",
        status: "Healthy",
        startedAt: "2026-09-28T11:00:00.000Z",
        lastHeartbeatAt: "2026-09-28T12:00:00.000Z",
        lastSuccessAt: "2026-09-28T12:00:00.000Z",
        consecutiveFailures: 0,
        lastErrorType: null,
    }],
    hasUnhealthyWorkers: false,
    queueMetrics: {
        available: true,
        machineConnections: { monitored: 3, online: 2, staleOnline: 1, onlineWithoutOwner: 0 },
        deviceMessageInbox: { Pending: 2, Retry: 1, Processing: 0, Failed: 0, Processed: 10 },
        oldestDeviceMessageAgeSeconds: 125,
        telemetryCommandDispatches: { Pending: 1, Processing: 1, Failed: 0 },
        oldestPendingTelemetryDispatchAgeSeconds: 40,
        oldestProcessingTelemetryDispatchAgeSeconds: 80,
        webhookInbox: { Pending: 0, Retry: 0, Processing: 0, Failed: 0, Processed: 4 },
        oldestWebhookAgeSeconds: null,
        pendingPayments: 1,
        oldestPendingPaymentAgeSeconds: 3600,
    },
};

const paymentReconciliation = {
    counts: { pending: 1, reconciliationRequired: 0 },
    items: [{
        id: "payment-1",
        companyId: "company-1",
        companyName: "Hospital de teste",
        machineId: "machine-1",
        machineName: "Máquina recepção",
        amount: 12,
        status: "Pending" as const,
        createdAt: "2026-09-27T12:00:00.000Z",
    }],
    page: 1,
    pageSize: 20,
    totalItems: 1,
    totalPages: 1,
};

const openPaymentsTab = () => {
    fireEvent.mouseDown(screen.getByRole("tab", { name: "Pagamentos" }), { button: 0 });
};

const openTelemetryTab = () => {
    fireEvent.mouseDown(screen.getByRole("tab", { name: "Fila de Telemetria" }), { button: 0 });
};

const openOperationalHealthTab = () => {
    fireEvent.mouseDown(screen.getByRole("tab", { name: "Saúde operacional" }), { button: 0 });
};

const openAdminTab = (name: string) => {
    fireEvent.mouseDown(screen.getByRole("tab", { name }), { button: 0 });
};

const failedInbox = {
    counts: { Pending: 0, Retry: 0, Processing: 0, Failed: 1, Processed: 0 },
    failures: [{
        id: "inbox-1",
        machineId: "machine-1",
        serial: "serial-1",
        messageId: 42,
        receivedAt: "2026-09-27T12:00:00.000Z",
        attempts: 3,
        lastError: "Timeout de teste",
    }],
    page: 1,
    pageSize: 20,
    totalFailed: 1,
    totalPages: 1,
};

describe("Admin operational recovery states", () => {
    beforeEach(() => {
        vi.clearAllMocks();
        api.getUsers.mockResolvedValue({ items: [], pageNumber: 1, pageSize: 8, totalItems: 0, totalPages: 0 });
        api.getCompanies.mockResolvedValue({ items: [], pageNumber: 1, pageSize: 8, totalItems: 0, totalPages: 0 });
        api.getGlobalSettings.mockResolvedValue({ applicationFeePercent: 5 });
        api.getAdminLogs.mockResolvedValue({ items: [], page: 1, pageSize: 10, totalItems: 0, totalPages: 0 });
        api.getDeviceMessageInbox.mockResolvedValue({
            counts: { Pending: 0, Retry: 0, Processing: 0, Failed: 0, Processed: 0 },
            failures: [], page: 1, pageSize: 20, totalFailed: 0, totalPages: 0,
        });
        api.getReconciliationQueue.mockResolvedValue(emptyReconciliation);
        api.getWorkerHealth.mockResolvedValue(workerHealthSummary);
    });

    it("shows worker status, machine connections and queue age in operational health", async () => {
        render(<MemoryRouter><Admin /></MemoryRouter>);
        await waitFor(() => expect(api.getWorkerHealth).toHaveBeenCalledTimes(1));
        openOperationalHealthTab();

        expect(await screen.findByText("Processamento da inbox MDB")).toBeInTheDocument();
        expect(screen.getByText("Saudável")).toBeInTheDocument();
        expect(screen.getByRole("status")).toHaveTextContent("Há máquinas online sem atualização recente");
        expect(screen.getByText("Máquinas online")).toBeInTheDocument();
        expect(screen.getByText(/Item pendente mais antigo: 2 min/)).toBeInTheDocument();
        expect(screen.getByText("Inbox MDB")).toBeInTheDocument();
        expect(screen.getByText("Mais antigo: 1 h 0 min")).toBeInTheDocument();
    });

    it("shows worker health as unavailable on a failed first read and retries", async () => {
        api.getWorkerHealth.mockReset()
            .mockRejectedValueOnce(new Error("API indisponível"))
            .mockResolvedValueOnce(workerHealthSummary);

        render(<MemoryRouter><Admin /></MemoryRouter>);
        await waitFor(() => expect(api.getWorkerHealth).toHaveBeenCalledTimes(1));
        openOperationalHealthTab();

        expect(await screen.findByRole("alert")).toHaveTextContent("Não foi possível carregar a saúde operacional.");
        expect(screen.queryByText("Processamento da inbox MDB")).not.toBeInTheDocument();
        fireEvent.click(within(screen.getByRole("tabpanel")).getByRole("button", { name: "Atualizar" }));

        expect(await screen.findByText("Processamento da inbox MDB")).toBeInTheDocument();
        expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    });

    it("keeps worker diagnostics visible when database queue metrics are unavailable", async () => {
        api.getWorkerHealth.mockResolvedValue({
            ...workerHealthSummary,
            queueMetrics: { available: false },
        });

        render(<MemoryRouter><Admin /></MemoryRouter>);
        openOperationalHealthTab();

        expect(await screen.findByText("Processamento da inbox MDB")).toBeInTheDocument();
        expect(screen.getByRole("alert")).toHaveTextContent("O banco não respondeu à consulta das filas.");
        expect(screen.queryByText("Máquinas online")).not.toBeInTheDocument();
    });

    it("keeps the last worker health snapshot visible when refresh fails", async () => {
        api.getWorkerHealth.mockReset()
            .mockResolvedValueOnce(workerHealthSummary)
            .mockRejectedValueOnce(new Error("API indisponível"));

        render(<MemoryRouter><Admin /></MemoryRouter>);
        await waitFor(() => expect(api.getWorkerHealth).toHaveBeenCalledTimes(1));
        openOperationalHealthTab();
        expect(await screen.findByText("Processamento da inbox MDB")).toBeInTheDocument();
        fireEvent.click(within(screen.getByRole("tabpanel")).getByRole("button", { name: "Atualizar" }));

        expect(await screen.findByRole("alert")).toHaveTextContent("Os dados exibidos podem estar desatualizados.");
        expect(screen.getByText("Processamento da inbox MDB")).toBeInTheDocument();
    });

    it("does not report a failed first read as zero or an empty payment queue and retries", async () => {
        api.getReconciliationQueue
            .mockReset()
            .mockRejectedValueOnce(new Error("API indisponível"))
            .mockResolvedValueOnce(emptyReconciliation);

        render(<MemoryRouter><Admin /></MemoryRouter>);
        await waitFor(() => expect(api.getReconciliationQueue).toHaveBeenCalledTimes(1));
        openPaymentsTab();

        expect(await screen.findByRole("alert")).toHaveTextContent("Não foi possível carregar a fila de conciliação.");
        expect(screen.getAllByText("—")).toHaveLength(2);
        expect(screen.queryByText("Nenhum pagamento pendente de conciliação.")).not.toBeInTheDocument();

        fireEvent.click(screen.getByRole("button", { name: "Atualizar" }));
        expect(await screen.findByText("Nenhum pagamento pendente de conciliação.")).toBeInTheDocument();
        expect(screen.queryByRole("alert")).not.toBeInTheDocument();
        expect(screen.getAllByText("0")).toHaveLength(2);
    });

    it("keeps the last successful queue visible with a stale-data warning after refresh fails", async () => {
        api.getReconciliationQueue
            .mockResolvedValueOnce(paymentReconciliation)
            .mockRejectedValueOnce(new Error("API indisponível"));

        render(<MemoryRouter><Admin /></MemoryRouter>);
        await waitFor(() => expect(api.getReconciliationQueue).toHaveBeenCalledTimes(1));
        openPaymentsTab();
        expect(await screen.findByText("Hospital de teste")).toBeInTheDocument();

        fireEvent.click(screen.getByRole("button", { name: "Atualizar" }));
        expect(await screen.findByRole("alert")).toHaveTextContent("Os dados exibidos podem estar desatualizados.");
        expect(screen.getByText("Hospital de teste")).toBeInTheDocument();
        expect(screen.queryByText("Nenhum pagamento pendente de conciliação.")).not.toBeInTheDocument();
    });

    it("does not report a failed first telemetry read as zero or an empty queue and retries", async () => {
        api.getDeviceMessageInbox
            .mockReset()
            .mockRejectedValueOnce(new Error("API indisponível"))
            .mockResolvedValueOnce({
                ...failedInbox,
                counts: { Pending: 0, Retry: 0, Processing: 0, Failed: 0, Processed: 0 },
                failures: [],
            });

        render(<MemoryRouter><Admin /></MemoryRouter>);
        await waitFor(() => expect(api.getDeviceMessageInbox).toHaveBeenCalledTimes(1));
        openTelemetryTab();

        expect(await screen.findByRole("alert")).toHaveTextContent("Não foi possível carregar a fila de telemetria.");
        expect(screen.getAllByText("—")).toHaveLength(5);
        expect(screen.queryByText("Nenhuma mensagem em falha.")).not.toBeInTheDocument();

        fireEvent.click(within(screen.getByRole("tabpanel")).getByRole("button", { name: "Atualizar" }));
        expect(await screen.findByText("Nenhuma mensagem em falha.")).toBeInTheDocument();
        expect(screen.queryByRole("alert")).not.toBeInTheDocument();
        expect(within(screen.getByRole("tabpanel")).getAllByText("0")).toHaveLength(5);
    });

    it("keeps the last successful telemetry failures visible when refresh fails", async () => {
        api.getDeviceMessageInbox
            .mockResolvedValueOnce(failedInbox)
            .mockRejectedValueOnce(new Error("API indisponível"));

        render(<MemoryRouter><Admin /></MemoryRouter>);
        await waitFor(() => expect(api.getDeviceMessageInbox).toHaveBeenCalledTimes(1));
        openTelemetryTab();
        expect(await screen.findByText("serial-1")).toBeInTheDocument();

        fireEvent.click(within(screen.getByRole("tabpanel")).getByRole("button", { name: "Atualizar" }));
        expect(await screen.findByRole("alert")).toHaveTextContent("Os dados exibidos podem estar desatualizados.");
        expect(screen.getByText("serial-1")).toBeInTheDocument();
        expect(screen.queryByText("Nenhuma mensagem em falha.")).not.toBeInTheDocument();
    });

    it("shows user loading failure and can recover without presenting an empty list", async () => {
        api.getUsers.mockReset()
            .mockRejectedValueOnce(new Error("API indisponível"))
            .mockResolvedValueOnce({ items: [], pageNumber: 1, pageSize: 8, totalItems: 0, totalPages: 0 });

        render(<MemoryRouter><Admin /></MemoryRouter>);
        await waitFor(() => expect(api.getUsers).toHaveBeenCalledTimes(1));
        expect(await screen.findByRole("alert")).toHaveTextContent("Não foi possível carregar os usuários.");
        expect(screen.queryByText("Nenhum usuário localizado.")).not.toBeInTheDocument();

        fireEvent.click(screen.getByRole("button", { name: "Tentar novamente" }));
        expect(await screen.findByText("Nenhum usuário localizado.")).toBeInTheDocument();
        expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    });

    it("shows company and audit-log failures as unavailable with retry", async () => {
        api.getCompanies.mockReset()
            .mockRejectedValueOnce(new Error("API indisponível"))
            .mockResolvedValueOnce({ items: [], pageNumber: 1, pageSize: 8, totalItems: 0, totalPages: 0 });
        api.getAdminLogs.mockReset()
            .mockRejectedValueOnce(new Error("API indisponível"))
            .mockResolvedValueOnce({ items: [], page: 1, pageSize: 10, totalItems: 0, totalPages: 0 });

        render(<MemoryRouter><Admin /></MemoryRouter>);
        await waitFor(() => {
            expect(api.getCompanies).toHaveBeenCalledTimes(1);
            expect(api.getAdminLogs).toHaveBeenCalledTimes(1);
        });

        openAdminTab("Empresas");
        expect(await screen.findByRole("alert")).toHaveTextContent("Não foi possível carregar as empresas.");
        expect(screen.queryByText("Nenhuma empresa localizada.")).not.toBeInTheDocument();
        fireEvent.click(within(screen.getByRole("tabpanel")).getByRole("button", { name: "Tentar novamente" }));
        expect(await screen.findByText("Nenhuma empresa localizada.")).toBeInTheDocument();

        openAdminTab("Logs de Transações");
        expect(await screen.findByRole("alert")).toHaveTextContent("Não foi possível carregar os logs administrativos.");
        expect(screen.queryByText("Nenhum log registrado na base de dados.")).not.toBeInTheDocument();
        fireEvent.click(within(screen.getByRole("tabpanel")).getByRole("button", { name: "Tentar novamente" }));
        expect(await screen.findByText("Nenhum log registrado na base de dados.")).toBeInTheDocument();
    });

    it("shows unavailable payment settings and retries their initial read", async () => {
        api.getGlobalSettings.mockReset()
            .mockRejectedValueOnce(new Error("API indisponível"))
            .mockResolvedValueOnce({ applicationFeePercent: 7 });

        render(<MemoryRouter><Admin /></MemoryRouter>);
        await waitFor(() => expect(api.getGlobalSettings).toHaveBeenCalledTimes(1));
        openAdminTab("Pagamentos");
        expect(await screen.findByRole("alert")).toHaveTextContent("Não foi possível carregar as configurações de pagamento.");
        expect(screen.getByText("Configurações indisponíveis.")).toBeInTheDocument();
        expect(screen.queryByLabelText("Taxa de Comissão do Site (%)")).not.toBeInTheDocument();

        fireEvent.click(within(screen.getByRole("tabpanel")).getByRole("button", { name: "Tentar novamente" }));
        expect(await screen.findByLabelText("Taxa de Comissão do Site (%)")).toBeInTheDocument();
        expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    });

    it("does not mistake integration detail failures for an unconfigured company", async () => {
        api.getCompanies.mockResolvedValue({
            items: [{
                id: "company-1",
                name: "Empresa de teste",
                activationStatus: "Active",
                createdBy: "Admin",
                createdByEmail: "admin@example.test",
                partners: [],
                createdAt: "2026-09-27T12:00:00.000Z",
                enabledIntegrations: ["Mercado Pago"],
            }],
            pageNumber: 1,
            pageSize: 8,
            totalItems: 1,
            totalPages: 1,
        });
        api.getCompanyIntegration.mockReset()
            .mockRejectedValueOnce(new Error("API indisponível"))
            .mockResolvedValueOnce({});

        render(<MemoryRouter><Admin /></MemoryRouter>);
        openAdminTab("Empresas");
        fireEvent.click(await screen.findByRole("button", { name: "Detalhes" }));

        expect(await screen.findByRole("alert")).toHaveTextContent("Não foi possível carregar os detalhes da integração.");
        expect(screen.queryByText("Esta empresa não possui nenhuma integração com o Mercado Pago configurada.")).not.toBeInTheDocument();
        fireEvent.click(screen.getByRole("button", { name: "Tentar novamente" }));
        expect(await screen.findByText("Status da Integração:")).toBeInTheDocument();
        expect(api.getCompanyIntegration).toHaveBeenCalledTimes(2);
    });
});
