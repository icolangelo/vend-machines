import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import Payments from "@/pages/Payments";
import type { PaymentTransaction, PaymentTransactionHistoryPage, TransactionTelemetryLog } from "@/lib/api";

const mocks = vi.hoisted(() => ({
    getMachines: vi.fn(),
    getPaymentTransactionHistory: vi.fn(),
    getTransactionLogs: vi.fn(),
}));

vi.mock("@/lib/api", () => ({
    getMachines: mocks.getMachines,
    getPaymentTransactionHistory: mocks.getPaymentTransactionHistory,
    getTransactionLogs: mocks.getTransactionLogs,
}));
vi.mock("@/components/AppSidebar", () => ({ AppSidebar: () => <nav aria-label="Navegação" /> }));

const transaction: PaymentTransaction = {
    id: "transaction-001",
    machineId: "machine-001",
    machine: { id: "machine-001", name: "Máquina de teste", serialNumber: "serial-001" },
    amount: 30,
    applicationFee: 0.63,
    status: "Chargeback",
    mercadoPagoStatus: "charged_back",
    createdAt: "2026-10-25T12:00:00Z",
};

const makePage = (items: PaymentTransaction[], page = 1, total = items.length): PaymentTransactionHistoryPage => ({
    items,
    total,
    page,
    pageSize: 25,
    totalPages: Math.max(1, Math.ceil(total / 25)),
});

function renderPayments(initialEntry = "/payments") {
    return render(
        <MemoryRouter initialEntries={[initialEntry]}>
            <Routes><Route path="/payments" element={<Payments />} /></Routes>
        </MemoryRouter>,
    );
}

describe("Payments", () => {
    beforeEach(() => {
        vi.clearAllMocks();
        mocks.getMachines.mockResolvedValue([]);
        mocks.getPaymentTransactionHistory.mockResolvedValue(makePage([transaction], 1, 26));
        mocks.getTransactionLogs.mockResolvedValue([] as TransactionTelemetryLog[]);
    });

    it("shows payment status, financial breakdown, and the distinction from delivered sales", async () => {
        renderPayments();

        expect(await screen.findByText("Contestado (chargeback)")).toBeInTheDocument();
        expect(screen.getByText("Máquina de teste")).toBeInTheDocument();
        expect(screen.getByText(/Valor cobrado/)).toBeInTheDocument();
        expect(screen.getByText(/Taxa da plataforma: R\$\s*0,63/)).toBeInTheDocument();
        expect(screen.getByText(/Após taxa da plataforma: R\$\s*29,37/)).toBeInTheDocument();
        expect(screen.getByText(/Uma cobrança não representa uma venda até a entrega confirmada/)).toBeInTheDocument();
        expect(screen.getByText("Exibindo 1–25 de 26 transações")).toBeInTheDocument();
    });

    it("loads transaction events when the user expands a transaction", async () => {
        mocks.getTransactionLogs.mockResolvedValue([{
            id: "log-001",
            transactionId: transaction.id,
            timestamp: "2026-10-25T12:01:00Z",
            logType: "AckReceived",
            message: "Entrega confirmada.",
        } satisfies TransactionTelemetryLog]);
        renderPayments();

        fireEvent.click(await screen.findByRole("button", { name: "Ver eventos da transação" }));

        expect(await screen.findByText("Entrega confirmada.")).toBeInTheDocument();
        expect(mocks.getTransactionLogs).toHaveBeenCalledWith(transaction.id);
    });

    it("loads the next history page when requested", async () => {
        mocks.getPaymentTransactionHistory.mockImplementation(async (page: number) =>
            makePage(page === 1 ? [transaction] : [], page, 26));
        renderPayments();

        fireEvent.click(await screen.findByRole("button", { name: "Próxima" }));

        await waitFor(() => expect(mocks.getPaymentTransactionHistory).toHaveBeenLastCalledWith(2, 25, undefined, undefined, undefined));
        expect(await screen.findByText("Página 2 de 2")).toBeInTheDocument();
        expect(screen.getByText("Exibindo 26–26 de 26 transações")).toBeInTheDocument();
    });

    it("applies the selected status filter to the history request", async () => {
        renderPayments();

        fireEvent.click(await screen.findByRole("combobox", { name: "Filtrar pagamentos por estado" }));
        fireEvent.click(await screen.findByRole("option", { name: "Contestado (chargeback)" }));

        await waitFor(() => expect(mocks.getPaymentTransactionHistory).toHaveBeenLastCalledWith(1, 25, undefined, undefined, "Chargeback"));
    });

    it("applies the selected machine filter to the history request", async () => {
        mocks.getMachines.mockResolvedValue([{
            id: "machine-001",
            name: "Máquina de teste",
            clientName: "Empresa de teste",
            location: "Unidade de teste",
            status: "online",
            stockLevel: 50,
            revenue30d: 0,
            totalSales30d: 0,
            lastSync: "2026-10-25T12:00:00Z",
            serialNumber: "serial-001",
        }]);
        renderPayments();

        fireEvent.click(await screen.findByRole("combobox", { name: "Filtrar pagamentos por máquina" }));
        fireEvent.click(await screen.findByRole("option", { name: "Máquina de teste · machine-001" }));

        await waitFor(() => expect(mocks.getPaymentTransactionHistory).toHaveBeenLastCalledWith(1, 25, "machine-001", undefined, undefined));
    });

    it("shows an unavailable state when the initial history request fails", async () => {
        mocks.getPaymentTransactionHistory.mockRejectedValueOnce(new Error("API indisponível"));
        renderPayments();

        expect(await screen.findByRole("alert")).toHaveTextContent("O histórico está indisponível.");
        expect(screen.getByText("Não foi possível consultar as transações.")).toBeInTheDocument();
    });

    it("keeps the current transactions visible when a refresh fails", async () => {
        renderPayments();

        expect(await screen.findByText("Contestado (chargeback)")).toBeInTheDocument();
        mocks.getPaymentTransactionHistory.mockRejectedValueOnce(new Error("API indisponível"));
        fireEvent.click(screen.getByRole("button", { name: "Atualizar" }));

        expect(await screen.findByRole("alert")).toHaveTextContent("Não foi possível atualizar. Os dados exibidos podem estar desatualizados.");
        expect(screen.getByText("Contestado (chargeback)")).toBeInTheDocument();
    });

    it("shows an explicit empty state when no transactions match", async () => {
        mocks.getPaymentTransactionHistory.mockResolvedValueOnce(makePage([]));
        renderPayments();

        expect(await screen.findByText("Nenhuma transação encontrada.")).toBeInTheDocument();
    });
});
