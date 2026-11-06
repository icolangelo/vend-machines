import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
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
    getPaymentWebhookInbox: vi.fn(),
    getReconciliationQueue: vi.fn(),
    getWorkerHealth: vi.fn(),
    getLegacyMachineConnections: vi.fn(),
    retryDeviceMessageInbox: vi.fn(),
    retryPaymentWebhook: vi.fn(),
    resolvePaymentReconciliation: vi.fn(),
}));

vi.mock("@/lib/api", () => api);

vi.mock("@/components/AppSidebar", () => ({
    AppSidebar: () => <nav aria-label="Navegação" />,
}));

vi.mock("@/contexts/useAuth", () => ({
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

const emptyPaymentWebhookInbox = {
    items: [],
    page: 1,
    pageSize: 20,
    totalItems: 0,
    totalPages: 0,
};
const failedWebhookInbox = {
    ...emptyPaymentWebhookInbox,
    items: [{ id: "webhook-1", paymentId: "payment-1", eventType: "payment", eventAction: "updated", format: "json", authenticationMode: "signature", status: "Failed", attempts: 3, receivedAt: "2026-09-27T12:00:00Z", nextAttemptAt: null, lastError: "Falha de teste" }],
    totalItems: 1, totalPages: 1,
};

const workerHealthSummary = {
    generatedAt: "2026-09-28T12:00:00.000Z",
    sourceCommit: "0123456789abcdef0123456789abcdef01234567",
    workers: [{
        name: "telemetry-inbox",
        status: "Healthy",
        startedAt: "2026-09-28T11:00:00.000Z",
        lastHeartbeatAt: "2026-09-28T12:00:00.000Z",
        lastSuccessAt: "2026-09-28T12:00:00.000Z",
        consecutiveFailures: 0,
        lastErrorType: null,
    }, {
        name: "telemetry-panel-event-relay",
        status: "Healthy",
        startedAt: "2026-09-28T11:00:00.000Z",
        lastHeartbeatAt: "2026-09-28T12:00:00.000Z",
        lastSuccessAt: "2026-09-28T12:00:00.000Z",
        consecutiveFailures: 0,
        lastErrorType: null,
    }, {
        name: "telemetry-command-dispatch",
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
        machineConnections: {
            monitored: 3,
            online: 2,
            staleOnline: 1,
            onlineWithoutOwner: 0,
            legacyRelay: { eligibleConnections: 0, configured: false, healthy: null },
        },
        deviceMessageInbox: { Pending: 2, Retry: 1, Processing: 0, Failed: 0, Processed: 10 },
        oldestDeviceMessageAgeSeconds: 125,
        telemetryCommandDispatches: { Pending: 1, Processing: 1, Failed: 0 },
        oldestPendingTelemetryDispatchAgeSeconds: 40,
        oldestProcessingTelemetryDispatchAgeSeconds: 80,
        webhookInbox: { Pending: 0, Retry: 0, Processing: 0, Failed: 0, Processed: 4 },
        oldestWebhookAgeSeconds: null,
        financialOutbox: { pending: 0, processing: 0, failed: 0, processed: 0, deferred_legacy_overlap: 0 },
        oldestPendingFinancialOutboxAgeSeconds: null,
        oldestDeferredLegacyFinancialOutboxAgeSeconds: null,
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

const actionableReconciliation = {
    ...paymentReconciliation,
    counts: { pending: 0, reconciliationRequired: 1 },
    items: [{ ...paymentReconciliation.items[0], status: "ReconciliationRequired" as const, reconciliationReason: "provider_approval_after_rejected_or_cancelled", mercadoPagoStatus: "approved", mercadoPagoStatusDetail: "accredited" }],
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
        api.getPaymentWebhookInbox.mockResolvedValue(emptyPaymentWebhookInbox);
        api.getReconciliationQueue.mockResolvedValue(emptyReconciliation);
        api.getWorkerHealth.mockResolvedValue(workerHealthSummary);
        api.getLegacyMachineConnections.mockResolvedValue({ generatedAt: "2026-10-30T12:00:00Z", relay: { configured: true, healthy: true }, items: [], page: 1, pageSize: 50, totalItems: 0, totalPages: 0 });
    });

    it("shows worker status, machine connections and queue age in operational health", async () => {
        render(<MemoryRouter><Admin /></MemoryRouter>);
        await waitFor(() => expect(api.getWorkerHealth).toHaveBeenCalledTimes(1));
        openOperationalHealthTab();
        expect(await screen.findByText("0123456789abcdef0123456789abcdef01234567")).toBeInTheDocument();

        expect(await screen.findByText("Processamento da inbox MDB")).toBeInTheDocument();
        expect(screen.getByText("Eventos do painel entre instâncias")).toBeInTheDocument();
        expect(screen.getByText("Roteamento de comandos MDB")).toBeInTheDocument();
        expect(screen.getAllByText("Saudável")).toHaveLength(3);
        expect(screen.getByRole("status")).toHaveTextContent("Há máquinas conectadas sem atualização recente");
        expect(screen.getByText("Máquinas conectadas")).toBeInTheDocument();
        expect(screen.getByText(/Item pendente mais antigo: 2 min/)).toBeInTheDocument();
        expect(screen.getByText("Inbox MDB")).toBeInTheDocument();
        expect(screen.getByText("Mais antigo: 1 h 0 min")).toBeInTheDocument();
    });

    it("shows healthy legacy sockets separately from release-blocking routes", async () => {
        api.getWorkerHealth.mockResolvedValue({
            ...workerHealthSummary,
            queueMetrics: {
                ...workerHealthSummary.queueMetrics,
                machineConnections: {
                    monitored: 3,
                    online: 2,
                    staleOnline: 0,
                    onlineWithoutOwner: 2,
                    legacyRelay: { eligibleConnections: 2, configured: true, healthy: true },
                    readinessGate: {
                        onlineInGateScope: 2,
                        unroutable: 0,
                        withoutOwner: 0,
                        withoutCommandRouting: 0,
                        companyMismatch: 0,
                    },
                },
            },
        });

        render(<MemoryRouter><Admin /></MemoryRouter>);
        openOperationalHealthTab();

        expect(await screen.findByText(/Há 2 conexão\(ões\) ainda pertencente\(s\) à API antiga/)).toHaveAttribute("role", "status");
        expect(screen.getByText("No processo anterior")).toBeInTheDocument();
        const routeBlocker = screen.getByText("Sem rota segura");
        expect(routeBlocker.parentElement).toHaveTextContent("0");
        expect(screen.queryByText(/conexões sem rota MDB segura/)).not.toBeInTheDocument();
    });

    it("lists safe machine details and paginates legacy connections", async () => {
        api.getLegacyMachineConnections.mockReset()
            .mockResolvedValueOnce({
                generatedAt: "2026-10-30T12:00:00Z",
                relay: { configured: true, healthy: true },
                items: [{ machineId: "legacy-1", machineName: "Máquina Hospital", serialNumber: "MDB-001", companyName: "Hospital São Luiz", lastSeenAt: "2026-10-30T11:59:00Z", activeSessionState: "AwaitingPayment", activeSessionStartedAt: "2026-10-30T11:58:00Z" }],
                page: 1, pageSize: 50, totalItems: 51, totalPages: 2,
            })
            .mockResolvedValue({ generatedAt: "2026-10-30T12:01:00Z", relay: { configured: true, healthy: true }, items: [], page: 2, pageSize: 50, totalItems: 51, totalPages: 2 });

        render(<MemoryRouter><Admin /></MemoryRouter>);
        openOperationalHealthTab();
        expect(await screen.findByText("Máquinas ainda conectadas à API anterior")).toBeInTheDocument();
        expect(screen.getByText("Hospital São Luiz")).toBeInTheDocument();
        expect(screen.getByText("MDB-001")).toBeInTheDocument();
        expect(screen.getByText(/AwaitingPayment/)).toBeInTheDocument();

        fireEvent.click(screen.getByRole("button", { name: /Próxima/ }));
        await waitFor(() => expect(api.getLegacyMachineConnections).toHaveBeenLastCalledWith(2, 50));
        expect(await screen.findByText("Nenhuma conexão nesta página.")).toBeInTheDocument();
    });

    it("surfaces deferred financial operations and their age during legacy overlap", async () => {
        api.getWorkerHealth.mockResolvedValue({
            ...workerHealthSummary,
            queueMetrics: {
                ...workerHealthSummary.queueMetrics,
                financialOutbox: { pending: 0, processing: 0, failed: 0, processed: 0, deferred_legacy_overlap: 2 },
                oldestDeferredLegacyFinancialOutboxAgeSeconds: 7200,
            },
        });

        render(<MemoryRouter><Admin /></MemoryRouter>);
        openOperationalHealthTab();

        expect(await screen.findByText(/Há 2 operação\(ões\) financeira\(s\) adiadas durante a sobreposição/))
            .toHaveAttribute("role", "status");
        expect(screen.getByText((_, element) => element?.textContent === "Adiadas na sobreposição: 2")).toBeInTheDocument();
        expect(screen.getByText((_, element) => element?.textContent === "Operação adiada mais antiga: 2 h 0 min")).toBeInTheDocument();
    });

    it("keeps unknown worker identifiers visible and translates unknown statuses safely", async () => {
        api.getWorkerHealth.mockResolvedValue({
            ...workerHealthSummary,
            workers: [
                ...workerHealthSummary.workers,
                {
                    ...workerHealthSummary.workers[0],
                    name: "future-worker",
                    status: "Starting",
                },
            ],
        });

        render(<MemoryRouter><Admin /></MemoryRouter>);
        openOperationalHealthTab();

        expect(await screen.findByText("Worker não identificado (future-worker)")).toBeInTheDocument();
        expect(screen.getByText("Desconhecido")).toBeInTheDocument();
        expect(screen.queryByText("Starting")).not.toBeInTheDocument();
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
        expect(screen.queryByText("Máquinas conectadas")).not.toBeInTheDocument();
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

    it("shows a Portuguese reason when an approval contradicts a rejected payment", async () => {
        api.getReconciliationQueue.mockReset().mockResolvedValue({
            ...paymentReconciliation,
            counts: { pending: 0, reconciliationRequired: 1 },
            items: [{
                ...paymentReconciliation.items[0],
                status: "ReconciliationRequired",
                reconciliationReason: "provider_approval_after_rejected_or_cancelled",
                mercadoPagoStatus: "approved",
                mercadoPagoStatusDetail: "accredited",
            }],
        });

        render(<MemoryRouter><Admin /></MemoryRouter>);
        openPaymentsTab();

        expect(await screen.findByText("Aprovação após cobrança recusada ou cancelada")).toBeInTheDocument();
    });

    it("disables financial decisions when refreshing the queue fails", async () => {
        api.getReconciliationQueue.mockReset().mockResolvedValueOnce(actionableReconciliation).mockRejectedValueOnce(new Error("consulta indisponível"));
        render(<MemoryRouter><Admin /></MemoryRouter>);
        openPaymentsTab();
        expect(await screen.findByRole("button", { name: "Registrar conferência" })).toBeEnabled();
        fireEvent.click(screen.getByRole("button", { name: "Atualizar" }));
        expect(await screen.findByRole("alert")).toHaveTextContent("Os dados exibidos podem estar desatualizados.");
        expect(screen.getByRole("button", { name: "Registrar conferência" })).toBeDisabled();
    });

    it("requires a fresh review after an uncertain financial decision", async () => {
        api.getReconciliationQueue.mockReset().mockResolvedValue(actionableReconciliation);
        api.resolvePaymentReconciliation.mockReset().mockRejectedValueOnce(new TypeError("Failed to fetch"));
        render(<MemoryRouter><Admin /></MemoryRouter>);
        openPaymentsTab();
        fireEvent.click(await screen.findByRole("button", { name: "Registrar conferência" }));
        const dialog = screen.getByRole("dialog");
        fireEvent.change(within(dialog).getByLabelText("Referência do comprovante ou chamado"), { target: { value: "CASE-123" } });
        fireEvent.click(within(dialog).getByRole("checkbox"));
        fireEvent.click(within(dialog).getByRole("button", { name: "Registrar decisão" }));
        expect(await within(dialog).findByRole("alert")).toHaveTextContent("Não foi possível confirmar a decisão.");
        await waitFor(() => expect(api.getReconciliationQueue).toHaveBeenCalledTimes(2));
        expect(within(dialog).getByRole("button", { name: "Registrar decisão" })).toBeDisabled();
        expect(api.resolvePaymentReconciliation).toHaveBeenCalledTimes(1);
        fireEvent.click(within(dialog).getByRole("button", { name: "Voltar" }));
        await waitFor(() => expect(screen.getByRole("button", { name: "Registrar conferência" })).toBeEnabled());
    });

    it("blocks webhook reprocessing when the current queue cannot be refreshed", async () => {
        api.getPaymentWebhookInbox.mockReset().mockResolvedValueOnce(failedWebhookInbox).mockRejectedValueOnce(new Error("consulta indisponível"));
        render(<MemoryRouter><Admin /></MemoryRouter>);
        openAdminTab("Webhooks MP");
        expect(await screen.findByRole("button", { name: "Reprocessar" })).toBeEnabled();
        fireEvent.click(within(screen.getByRole("tabpanel")).getByRole("button", { name: "Atualizar" }));
        expect(await screen.findByRole("alert")).toHaveTextContent("Os dados exibidos podem estar desatualizados.");
        expect(screen.getByRole("button", { name: "Reprocessar" })).toBeDisabled();
    });

    it("refreshes after an uncertain webhook retry and blocks a repeat when the refresh fails", async () => {
        api.getPaymentWebhookInbox.mockReset().mockResolvedValueOnce(failedWebhookInbox).mockRejectedValueOnce(new Error("consulta indisponível"));
        api.retryPaymentWebhook.mockReset().mockRejectedValueOnce(new TypeError("Failed to fetch"));
        const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
        const alert = vi.spyOn(window, "alert").mockImplementation(() => {});
        try {
            render(<MemoryRouter><Admin /></MemoryRouter>);
            openAdminTab("Webhooks MP");
            fireEvent.click(await screen.findByRole("button", { name: "Reprocessar" }));
            await waitFor(() => expect(alert).toHaveBeenCalledWith("Não foi possível confirmar o reprocessamento. Confira a fila atualizada antes de tentar novamente."));
            expect(await screen.findByRole("alert")).toHaveTextContent("Os dados exibidos podem estar desatualizados.");
            expect(screen.getByRole("button", { name: "Reprocessar" })).toBeDisabled();
            expect(api.retryPaymentWebhook).toHaveBeenCalledTimes(1);
        } finally {
            confirm.mockRestore();
            alert.mockRestore();
        }
    });

    it("ignores an old webhook response after the status filter changes", async () => {
        let resolveOld!: (value: unknown) => void;
        let resolveNew!: (value: unknown) => void;
        api.getPaymentWebhookInbox.mockReset()
            .mockImplementationOnce(() => new Promise(resolve => { resolveOld = resolve; }))
            .mockImplementationOnce(() => new Promise(resolve => { resolveNew = resolve; }));
        render(<MemoryRouter><Admin /></MemoryRouter>);
        openAdminTab("Webhooks MP");
        fireEvent.change(screen.getByLabelText("Estado do webhook"), { target: { value: "Processed" } });
        await waitFor(() => expect(api.getPaymentWebhookInbox).toHaveBeenCalledTimes(2));
        await act(async () => resolveNew(emptyPaymentWebhookInbox));
        expect(await screen.findByText("Nenhum webhook neste estado.")).toBeInTheDocument();
        await act(async () => resolveOld(failedWebhookInbox));
        expect(screen.queryByRole("button", { name: "Reprocessar" })).not.toBeInTheDocument();
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
        expect(screen.getByRole("button", { name: "Reprocessar" })).toBeDisabled();
        expect(screen.queryByText("Nenhuma mensagem em falha.")).not.toBeInTheDocument();
    });

    it("ignores an older telemetry refresh after a reprocessing response triggers a newer one", async () => {
        let resolveOld!: (value: unknown) => void;
        let resolveNew!: (value: unknown) => void;
        let finishRetry!: () => void;
        api.getDeviceMessageInbox.mockReset().mockResolvedValueOnce(failedInbox)
            .mockImplementationOnce(() => new Promise(resolve => { resolveOld = resolve; }))
            .mockImplementationOnce(() => new Promise(resolve => { resolveNew = resolve; }));
        api.retryDeviceMessageInbox.mockReset().mockImplementation(() => new Promise<void>(resolve => { finishRetry = resolve; }));
        const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
        const alert = vi.spyOn(window, "alert").mockImplementation(() => {});
        try {
            render(<MemoryRouter><Admin /></MemoryRouter>);
            openTelemetryTab();
            fireEvent.click(await screen.findByRole("button", { name: "Reprocessar" }));
            fireEvent.click(within(screen.getByRole("tabpanel")).getByRole("button", { name: "Atualizar" }));
            await waitFor(() => expect(api.getDeviceMessageInbox).toHaveBeenCalledTimes(2));
            await act(async () => finishRetry());
            await waitFor(() => expect(api.getDeviceMessageInbox).toHaveBeenCalledTimes(3));
            await act(async () => resolveNew({ ...failedInbox, failures: [], totalFailed: 0 }));
            expect(await screen.findByText("Nenhuma mensagem em falha.")).toBeInTheDocument();
            await act(async () => resolveOld(failedInbox));
            expect(screen.queryByText("serial-1")).not.toBeInTheDocument();
        } finally {
            confirm.mockRestore();
            alert.mockRestore();
        }
    });

    it("refreshes after an uncertain telemetry retry and blocks another attempt if the read fails", async () => {
        api.getDeviceMessageInbox.mockReset().mockResolvedValueOnce(failedInbox).mockRejectedValueOnce(new Error("consulta indisponível"));
        api.retryDeviceMessageInbox.mockReset().mockRejectedValueOnce(new TypeError("Failed to fetch"));
        const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
        const alert = vi.spyOn(window, "alert").mockImplementation(() => {});
        try {
            render(<MemoryRouter><Admin /></MemoryRouter>);
            openTelemetryTab();
            fireEvent.click(await screen.findByRole("button", { name: "Reprocessar" }));
            await waitFor(() => expect(alert).toHaveBeenCalledWith("Não foi possível confirmar o reprocessamento. Confira a fila atualizada antes de tentar novamente."));
            expect(await screen.findByRole("alert")).toHaveTextContent("Os dados exibidos podem estar desatualizados.");
            expect(screen.getByRole("button", { name: "Reprocessar" })).toBeDisabled();
            expect(api.retryDeviceMessageInbox).toHaveBeenCalledTimes(1);
        } finally {
            confirm.mockRestore();
            alert.mockRestore();
        }
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

    it("blocks editing and another save when the fee result and subsequent read are uncertain", async () => {
        api.getGlobalSettings.mockReset().mockResolvedValueOnce({ applicationFeePercent: 5 }).mockRejectedValueOnce(new Error("consulta indisponível"));
        api.updateGlobalSettings.mockReset().mockRejectedValueOnce(new TypeError("Failed to fetch"));
        const alert = vi.spyOn(window, "alert").mockImplementation(() => {});
        try {
            render(<MemoryRouter><Admin /></MemoryRouter>);
            openPaymentsTab();
            const input = await screen.findByLabelText("Taxa de Comissão do Site (%)");
            fireEvent.change(input, { target: { value: "7" } });
            fireEvent.click(screen.getByRole("button", { name: "Salvar taxa" }));
            await waitFor(() => expect(alert).toHaveBeenCalledWith("Não foi possível confirmar a alteração. Confira a taxa atualizada antes de tentar novamente."));
            expect(await screen.findByRole("alert")).toHaveTextContent("Não foi possível carregar as configurações de pagamento.");
            expect(input).toBeDisabled();
            expect(screen.getByRole("button", { name: "Salvar taxa" })).toBeDisabled();
            expect(api.updateGlobalSettings).toHaveBeenCalledTimes(1);
        } finally {
            alert.mockRestore();
        }
    });

    it("reads the actual fee after a successful save before allowing another edit", async () => {
        api.getGlobalSettings.mockReset().mockResolvedValueOnce({ applicationFeePercent: 5, isConfigured: true }).mockResolvedValueOnce({ applicationFeePercent: 7, isConfigured: true });
        api.updateGlobalSettings.mockReset().mockResolvedValueOnce({});
        const alert = vi.spyOn(window, "alert").mockImplementation(() => {});
        try {
            render(<MemoryRouter><Admin /></MemoryRouter>);
            openPaymentsTab();
            const input = await screen.findByLabelText("Taxa de Comissão do Site (%)");
            fireEvent.change(input, { target: { value: "7" } });
            fireEvent.click(screen.getByRole("button", { name: "Salvar taxa" }));
            await waitFor(() => expect(api.getGlobalSettings).toHaveBeenCalledTimes(2));
            expect(api.updateGlobalSettings).toHaveBeenCalledWith({ applicationFeePercent: 7 }, { applicationFeePercent: 5, isConfigured: true });
            await waitFor(() => expect(input).toBeEnabled());
            expect(input).toHaveValue(7);
        } finally {
            alert.mockRestore();
        }
    });

    it("does not submit a platform fee with more precision than the database supports", async () => {
        const alert = vi.spyOn(window, "alert").mockImplementation(() => {});
        render(<MemoryRouter><Admin /></MemoryRouter>);
        openAdminTab("Pagamentos");

        const feeInput = await screen.findByLabelText("Taxa de Comissão do Site (%)");
        fireEvent.change(feeInput, { target: { value: "12.345" } });
        fireEvent.click(screen.getByRole("button", { name: "Salvar taxa" }));

        expect(alert).toHaveBeenCalledWith(
            "A taxa da plataforma deve estar entre 0,01% e 99,99%, com no máximo duas casas decimais.",
        );
        expect(api.updateGlobalSettings).not.toHaveBeenCalled();
        alert.mockRestore();
    });

    it.each(["0", "100"])("does not submit a platform fee of %s%%", async (value) => {
        const alert = vi.spyOn(window, "alert").mockImplementation(() => {});
        render(<MemoryRouter><Admin /></MemoryRouter>);
        openAdminTab("Pagamentos");

        const feeInput = await screen.findByLabelText("Taxa de Comissão do Site (%)");
        fireEvent.change(feeInput, { target: { value } });
        fireEvent.click(screen.getByRole("button", { name: "Salvar taxa" }));

        expect(alert).toHaveBeenCalledWith(
            "A taxa da plataforma deve estar entre 0,01% e 99,99%, com no máximo duas casas decimais.",
        );
        expect(api.updateGlobalSettings).not.toHaveBeenCalled();
        alert.mockRestore();
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

    it("shows whether integration credentials exist without rendering their values", async () => {
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
        api.getCompanyIntegration.mockResolvedValue({
            companyId: "company-1",
            ownerName: "Admin",
            ownerCpf: "00000000000",
            ownerEmail: "admin@example.test",
            ownerPhone: "",
            businessName: "Empresa de teste",
            tradeName: "",
            cnpj: "00000000000100",
            businessEmail: "admin@example.test",
            businessPhone: "",
            accessToken: "APP_USR-SECRET-ACCESS-TOKEN••••",
            hasAccessToken: true,
            publicKey: "PUBLIC-KEY",
            clientSecret: "CLIENT-SECRET••••",
            hasClientSecret: true,
            hasRefreshToken: true,
            tokenFingerprint: "fingerprint-private-value",
            isActive: true,
        });

        render(<MemoryRouter><Admin /></MemoryRouter>);
        openAdminTab("Empresas");
        fireEvent.click(await screen.findByRole("button", { name: "Detalhes" }));

        expect(await screen.findByText("Status da Integração:")).toBeInTheDocument();
        expect(screen.getByText("Configurado")).toBeInTheDocument();
        expect(screen.getByText("Configurada")).toBeInTheDocument();
        expect(screen.queryByText(/SECRET-ACCESS-TOKEN/)).not.toBeInTheDocument();
        expect(screen.queryByText(/CLIENT-SECRET/)).not.toBeInTheDocument();
        expect(screen.queryByText(/Fingerprint/i)).not.toBeInTheDocument();
        expect(screen.queryByText(/fingerprint-private-value/i)).not.toBeInTheDocument();
    });

    it("never shows a previous company's integration after opening another company", async () => {
        const company = (id: string, name: string) => ({ id, name, activationStatus: "Active", createdBy: "Admin", createdByEmail: "admin@example.test", partners: [], createdAt: "2026-09-27T12:00:00Z", enabledIntegrations: [] });
        api.getCompanies.mockReset().mockResolvedValue({ items: [company("a", "Empresa A"), company("b", "Empresa B")], pageNumber: 1, pageSize: 8, totalItems: 2, totalPages: 1 });
        let resolveFirst!: (value: unknown) => void;
        let resolveSecond!: (value: unknown) => void;
        api.getCompanyIntegration.mockReset()
            .mockImplementationOnce(() => new Promise(resolve => { resolveFirst = resolve; }))
            .mockImplementationOnce(() => new Promise(resolve => { resolveSecond = resolve; }));
        render(<MemoryRouter><Admin /></MemoryRouter>);
        openAdminTab("Empresas");
        fireEvent.click((await screen.findAllByRole("button", { name: "Detalhes" }))[0]);
        fireEvent.click(within(screen.getByRole("dialog")).getAllByRole("button", { name: "Fechar" })[0]);
        fireEvent.click(screen.getAllByRole("button", { name: "Detalhes" })[1]);
        await act(async () => resolveSecond({ ownerName: "Responsável B", businessName: "Empresa B", isActive: true }));
        expect(await screen.findByText("Responsável B")).toBeInTheDocument();
        await act(async () => resolveFirst({ ownerName: "Responsável A", businessName: "Empresa A", isActive: true }));
        expect(screen.getByText("Responsável B")).toBeInTheDocument();
        expect(screen.queryByText("Responsável A")).not.toBeInTheDocument();
        expect(screen.getByRole("dialog")).toHaveTextContent("Integração Mercado Pago - Empresa B");
    });
    it("ignores old user search responses after a newer result arrives", async () => {
        let resolveOld!: (value: unknown) => void;
        let resolveNew!: (value: unknown) => void;
        api.getUsers.mockReset().mockImplementation((_page: number, _size: number, term: string) => new Promise(resolve => {
            if (!term) resolveOld = resolve;
            else resolveNew = resolve;
        }));
        render(<MemoryRouter><Admin /></MemoryRouter>);
        await waitFor(() => expect(api.getUsers).toHaveBeenCalledWith(1, 8, ""));
        fireEvent.change(screen.getByLabelText("Pesquisar usuários"), { target: { value: "Bruna" } });
        fireEvent.click(screen.getByRole("button", { name: "Buscar" }));
        await waitFor(() => expect(api.getUsers).toHaveBeenCalledWith(1, 8, "Bruna"));
        const page = (id: string, name: string) => ({ items: [{ id, name, email: `${id}@example.test`, role: "Operator", companyName: "Empresa", createdAt: "2026-10-29T12:00:00Z" }], pageNumber: 1, pageSize: 8, totalItems: 1, totalPages: 1 });
        await act(async () => resolveNew(page("b", "Bruna")));
        expect(await screen.findByRole("combobox", { name: "Papel de Bruna" })).toBeEnabled();
        await act(async () => resolveOld(page("a", "Ana")));
        expect(screen.getByRole("combobox", { name: "Papel de Bruna" })).toBeEnabled();
        expect(screen.queryByText("Ana")).not.toBeInTheDocument();
    });

    it("keeps company actions disabled when a new search fails", async () => {
        api.getCompanies.mockReset().mockResolvedValueOnce({
            items: [{ id: "company-1", name: "Empresa A", activationStatus: "Pending", createdBy: "Admin", createdByEmail: "admin@example.test", partners: [], createdAt: "2026-10-29T12:00:00Z", enabledIntegrations: [] }],
            pageNumber: 1, pageSize: 8, totalItems: 1, totalPages: 1,
        }).mockRejectedValueOnce(new Error("falha sintética"));
        render(<MemoryRouter><Admin /></MemoryRouter>);
        openAdminTab("Empresas");
        expect(await screen.findByRole("button", { name: "Aprovar" })).toBeEnabled();
        fireEvent.change(screen.getByLabelText("Pesquisar empresas"), { target: { value: "Outra" } });
        fireEvent.click(screen.getByRole("button", { name: "Buscar" }));
        expect(await screen.findByRole("alert")).toHaveTextContent("Os dados exibidos podem estar desatualizados.");
        expect(screen.getByRole("button", { name: "Aprovar" })).toBeDisabled();
        expect(screen.getByRole("button", { name: "Recusar" })).toBeDisabled();
        expect(screen.getByRole("button", { name: "Detalhes" })).toBeDisabled();
    });

    it("refreshes the current user search after a role change finishes", async () => {
        const page = (id: string, name: string) => ({ items: [{ id, name, email: `${id}@example.test`, role: "Operator", companyName: "Empresa", createdAt: "2026-10-29T12:00:00Z" }], pageNumber: 1, pageSize: 8, totalItems: 1, totalPages: 1 });
        api.getUsers.mockReset().mockImplementation((_page: number, _size: number, term: string) => Promise.resolve(term ? page("b", "Bruna") : page("a", "Ana")));
        let finishChange!: () => void;
        api.updateUserRole.mockReset().mockImplementation(() => new Promise<void>(resolve => { finishChange = resolve; }));
        const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
        const alert = vi.spyOn(window, "alert").mockImplementation(() => {});
        try {
            render(<MemoryRouter><Admin /></MemoryRouter>);
            fireEvent.change(await screen.findByRole("combobox", { name: "Papel de Ana" }), { target: { value: "Admin" } });
            fireEvent.change(screen.getByLabelText("Pesquisar usuários"), { target: { value: "Bruna" } });
            fireEvent.click(screen.getByRole("button", { name: "Buscar" }));
            expect(await screen.findByRole("combobox", { name: "Papel de Bruna" })).toBeDisabled();
            await act(async () => finishChange());
            await waitFor(() => expect(api.getUsers).toHaveBeenCalledTimes(3));
            expect(api.getUsers.mock.calls[2]).toEqual([1, 8, "Bruna"]);
            await waitFor(() => expect(screen.getByRole("combobox", { name: "Papel de Bruna" })).toBeEnabled());
        } finally {
            confirm.mockRestore();
            alert.mockRestore();
        }
    });

    it("restores the selected role without a request when the administrator cancels", async () => {
        api.getUsers.mockReset().mockResolvedValue({ items: [{ id: "a", name: "Ana", email: "a@example.test", role: "Operator", companyName: "Empresa", createdAt: "2026-10-29T12:00:00Z" }], pageNumber: 1, pageSize: 8, totalItems: 1, totalPages: 1 });
        const confirm = vi.spyOn(window, "confirm").mockReturnValue(false);
        try {
            render(<MemoryRouter><Admin /></MemoryRouter>);
            const role = await screen.findByRole("combobox", { name: "Papel de Ana" });
            fireEvent.change(role, { target: { value: "Admin" } });
            expect(role).toHaveValue("Operator");
            expect(api.updateUserRole).not.toHaveBeenCalled();
            expect(api.getUsers).toHaveBeenCalledTimes(1);
        } finally {
            confirm.mockRestore();
        }
    });

    it("refreshes after an uncertain role change and disables stale actions if the refresh also fails", async () => {
        api.getUsers.mockReset().mockResolvedValueOnce({ items: [{ id: "a", name: "Ana", email: "a@example.test", role: "Operator", companyName: "Empresa", createdAt: "2026-10-29T12:00:00Z" }], pageNumber: 1, pageSize: 8, totalItems: 1, totalPages: 1 }).mockRejectedValueOnce(new Error("consulta indisponível"));
        api.updateUserRole.mockReset().mockRejectedValueOnce(new TypeError("Failed to fetch"));
        const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
        const alert = vi.spyOn(window, "alert").mockImplementation(() => {});
        try {
            render(<MemoryRouter><Admin /></MemoryRouter>);
            fireEvent.change(await screen.findByRole("combobox", { name: "Papel de Ana" }), { target: { value: "Admin" } });
            await waitFor(() => expect(alert).toHaveBeenCalledWith("Não foi possível confirmar a alteração. Confira a lista atualizada antes de tentar novamente."));
            expect(await screen.findByRole("alert")).toHaveTextContent("Os dados exibidos podem estar desatualizados.");
            expect(screen.getByRole("combobox", { name: "Papel de Ana" })).toBeDisabled();
        } finally {
            confirm.mockRestore();
            alert.mockRestore();
        }
    });
});
