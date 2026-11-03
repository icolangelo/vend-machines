import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { createHash, webcrypto } from "node:crypto";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import Inventory from "@/pages/Inventory";
import { inventoryPendingKey } from "@/lib/inventoryPending";

const { getInventorySlots, getInventoryMovements, getInventoryMovementReceipt, getMachines, getProducts, restockInventorySlot, useAuth, useImpersonation, InventoryMovementUncertainError } = vi.hoisted(() => ({
    getInventorySlots: vi.fn(),
    getInventoryMovements: vi.fn(),
    getInventoryMovementReceipt: vi.fn(),
    getMachines: vi.fn(),
    getProducts: vi.fn(),
    restockInventorySlot: vi.fn(),
    useAuth: vi.fn(),
    useImpersonation: vi.fn(),
    InventoryMovementUncertainError: class extends Error {},
}));

vi.mock("@/lib/api", () => ({
    getInventorySlots,
    getInventoryMovements,
    getInventoryMovementReceipt,
    getMachines,
    getProducts,
    restockInventorySlot,
    InventoryMovementUncertainError,
}));

vi.mock("@/components/AppSidebar", () => ({
    AppSidebar: () => <nav aria-label="Navegação" />,
}));

vi.mock("@/contexts/useAuth", () => ({
    useAuth,
}));

vi.mock("@/contexts/useImpersonation", () => ({
    useImpersonation,
}));

describe("Inventory", () => {
    beforeEach(() => {
        vi.clearAllMocks();
        sessionStorage.clear();
        restockInventorySlot.mockReset();
        getInventoryMovementReceipt.mockReset();
        useAuth.mockReturnValue({ user: { role: "Operator" } });
        useImpersonation.mockReturnValue({ isSuperAdmin: false, impersonatedCompany: null });
        getMachines.mockResolvedValue([]);
        getProducts.mockResolvedValue([]);
        getInventorySlots.mockResolvedValue([]);
        getInventoryMovements.mockResolvedValue({ items: [], page: 1, pageSize: 50, totalItems: 0, totalPages: 0 });
    });
    afterEach(() => vi.unstubAllGlobals());

    it("clears the previous company's inventory while the new one is loading", async () => {
        useAuth.mockReturnValue({ user: { id: "platform-1", role: "PlatformAdmin" } });
        useImpersonation.mockReturnValue({ isSuperAdmin: true, impersonatedCompany: { id: "company-a" } });
        const slot = { id: "slot-1", machineId: "machine-1", machineName: "Máquina A", itemNumber: 1, productId: "product-1", productName: "Água A", capacity: 20, currentStock: 5, lowStockThreshold: 2, isLowStock: false };
        let finishNew!: (value: unknown) => void;
        getInventorySlots.mockReset().mockResolvedValueOnce([slot]).mockImplementationOnce(() => new Promise(resolve => { finishNew = resolve; }));
        const { rerender } = render(<MemoryRouter><Inventory /></MemoryRouter>);
        expect(await screen.findByRole("button", { name: "Repor Água A" })).toBeEnabled();
        useImpersonation.mockReturnValue({ isSuperAdmin: true, impersonatedCompany: { id: "company-b" } });
        rerender(<MemoryRouter><Inventory /></MemoryRouter>);
        expect(screen.queryByText("Água A")).not.toBeInTheDocument();
        await act(async () => finishNew([]));
        expect(await screen.findByText("Nenhum compartimento cadastrado para esta empresa.")).toBeInTheDocument();
    });

    it("does not send a movement if the company changes while its reason is being hashed", async () => {
        let finishHash!: (value: ArrayBuffer) => void;
        vi.stubGlobal("crypto", { randomUUID: () => "b9d0ce8b-e9c2-43c5-b8e2-4f4f3e5a1001", subtle: { digest: () => new Promise<ArrayBuffer>(resolve => { finishHash = resolve; }) } });
        useAuth.mockReturnValue({ user: { id: "platform-1", role: "PlatformAdmin" } });
        useImpersonation.mockReturnValue({ isSuperAdmin: true, impersonatedCompany: { id: "company-a" } });
        getInventorySlots.mockResolvedValue([{ id: "slot-1", machineId: "machine-1", machineName: "Máquina A", itemNumber: 1, productId: "product-1", productName: "Água A", capacity: 20, currentStock: 5, lowStockThreshold: 2, isLowStock: false }]);
        const { rerender } = render(<MemoryRouter><Inventory /></MemoryRouter>);
        fireEvent.click(await screen.findByRole("button", { name: "Repor Água A" }));
        fireEvent.change(screen.getByLabelText("Motivo"), { target: { value: "Reposição" } });
        fireEvent.click(screen.getByRole("button", { name: "Registrar" }));
        useImpersonation.mockReturnValue({ isSuperAdmin: true, impersonatedCompany: { id: "company-b" } });
        rerender(<MemoryRouter><Inventory /></MemoryRouter>);
        await act(async () => finishHash(new Uint8Array(32).buffer));
        expect(restockInventorySlot).not.toHaveBeenCalled();
        expect(sessionStorage.getItem(inventoryPendingKey("platform-1", "company-a"))).toBeNull();
    });

    it("preserves the original company's pending marker if a submitted movement is uncertain after a switch", async () => {
        useAuth.mockReturnValue({ user: { id: "platform-1", role: "PlatformAdmin" } });
        useImpersonation.mockReturnValue({ isSuperAdmin: true, impersonatedCompany: { id: "company-a" } });
        getInventorySlots.mockResolvedValue([{ id: "slot-1", machineId: "machine-1", machineName: "Máquina A", itemNumber: 1, productId: "product-1", productName: "Água A", capacity: 20, currentStock: 5, lowStockThreshold: 2, isLowStock: false }]);
        let failRequest!: (reason: Error) => void;
        restockInventorySlot.mockImplementation(() => new Promise<void>((_resolve, reject) => { failRequest = reject; }));
        const { rerender } = render(<MemoryRouter><Inventory /></MemoryRouter>);
        fireEvent.click(await screen.findByRole("button", { name: "Repor Água A" }));
        fireEvent.change(screen.getByLabelText("Motivo"), { target: { value: "Reposição" } });
        fireEvent.click(screen.getByRole("button", { name: "Registrar" }));
        await waitFor(() => expect(restockInventorySlot).toHaveBeenCalledTimes(1));
        useImpersonation.mockReturnValue({ isSuperAdmin: true, impersonatedCompany: { id: "company-b" } });
        rerender(<MemoryRouter><Inventory /></MemoryRouter>);
        await act(async () => failRequest(new InventoryMovementUncertainError()));
        expect(sessionStorage.getItem(inventoryPendingKey("platform-1", "company-a"))).not.toBeNull();
        expect(sessionStorage.getItem(inventoryPendingKey("platform-1", "company-b"))).toBeNull();
        useImpersonation.mockReturnValue({ isSuperAdmin: true, impersonatedCompany: { id: "company-a" } });
        rerender(<MemoryRouter><Inventory /></MemoryRouter>);
        expect(await screen.findByText(/Há uma movimentação sem confirmação nesta sessão/)).toBeInTheDocument();
    });

    it("shows an unavailable state instead of an empty inventory after load failure, then retries", async () => {
        getInventorySlots
            .mockReset()
            .mockRejectedValueOnce(new Error("API indisponível"))
            .mockResolvedValueOnce([]);

        render(
            <MemoryRouter>
                <Inventory />
            </MemoryRouter>,
        );

        expect(await screen.findByRole("alert")).toHaveTextContent(
            "Não foi possível carregar o estoque. Confira sua conexão e tente novamente.",
        );
        expect(screen.queryByText("Nenhum compartimento cadastrado para esta empresa.")).not.toBeInTheDocument();
        expect(screen.queryByText("Compartimentos cadastrados")).not.toBeInTheDocument();

        fireEvent.click(screen.getByRole("button", { name: "Tentar novamente" }));
        await waitFor(() => expect(getInventorySlots).toHaveBeenCalledTimes(2));
        expect(await screen.findByText("Nenhum compartimento cadastrado para esta empresa.")).toBeInTheDocument();
        expect(screen.queryByRole("alert")).not.toBeInTheDocument();
        expect(screen.getByText("Compartimentos cadastrados")).toBeInTheDocument();
    });

    it("does not allow stock changes while the displayed data is stale", async () => {
        useAuth.mockReturnValue({ user: { id: "admin-1", companyId: "company-1", role: "Admin" } });
        const slot = { id: "slot-1", machineId: "machine-1", machineName: "Máquina 1", itemNumber: 1, productId: "product-1", productName: "Água", capacity: 20, currentStock: 5, lowStockThreshold: 2, isLowStock: false };
        getInventorySlots.mockResolvedValueOnce([slot]).mockRejectedValueOnce(new Error("API indisponível")).mockResolvedValueOnce([slot]);
        render(<MemoryRouter><Inventory /></MemoryRouter>);
        const restock = await screen.findByRole("button", { name: "Repor Água" });
        expect(restock).toBeEnabled();
        fireEvent.click(screen.getByRole("button", { name: "Atualizar estoque" }));
        expect(await screen.findByRole("alert")).toHaveTextContent("Os dados exibidos podem estar desatualizados");
        expect(restock).toBeDisabled();
        fireEvent.click(screen.getByRole("button", { name: "Tentar novamente" }));
        await waitFor(() => expect(restock).toBeEnabled());
    });

    it("blocks resubmission and asks the operator to inspect history when the result is uncertain", async () => {
        vi.stubGlobal("crypto", webcrypto);
        useAuth.mockReturnValue({ user: { id: "admin-1", companyId: "company-1", role: "Admin" } });
        getInventorySlots.mockResolvedValue([{ id: "slot-1", machineId: "machine-1", machineName: "Máquina 1", itemNumber: 1, productId: "product-1", productName: "Água", capacity: 20, currentStock: 5, lowStockThreshold: 2, isLowStock: false }]);
        restockInventorySlot.mockRejectedValue(new InventoryMovementUncertainError());
        const { unmount } = render(<MemoryRouter><Inventory /></MemoryRouter>);

        fireEvent.click(await screen.findByRole("button", { name: "Repor Água" }));
        fireEvent.change(screen.getByLabelText("Motivo"), { target: { value: "Reposição semanal" } });
        fireEvent.click(screen.getByRole("button", { name: "Registrar" }));

        expect(await screen.findByRole("alert")).toHaveTextContent("Não foi possível confirmar se a movimentação foi registrada");
        const marker = JSON.parse(sessionStorage.getItem(inventoryPendingKey("admin-1", "company-1"))!);
        expect(marker.requestId).toMatch(/^[0-9a-f-]{36}$/);
        expect(marker.reasonHash).toBe(createHash("sha256").update("Reposição semanal", "utf8").digest("hex"));
        expect(JSON.stringify(marker)).not.toContain("Reposição semanal");
        expect(screen.getByRole("button", { name: "Registrar" })).toBeDisabled();
        await waitFor(() => expect(getInventoryMovements).toHaveBeenCalledTimes(2));
        await waitFor(() => expect(restockInventorySlot).toHaveBeenCalledOnce());
        fireEvent.click(screen.getByRole("button", { name: "Fechar e conferir histórico" }));
        expect(screen.getByText("Movimentações recentes")).toBeInTheDocument();
        unmount();
        render(<MemoryRouter><Inventory /></MemoryRouter>);
        expect(await screen.findByText(/Há uma movimentação sem confirmação nesta sessão/)).toBeInTheDocument();
        const restock = await screen.findByRole("button", { name: "Repor Água" });
        expect(restock).toBeDisabled();
        const confirmHistory = screen.getByRole("button", { name: "Conferi o histórico" });
        await waitFor(() => expect(confirmHistory).toBeEnabled());
        fireEvent.click(confirmHistory);
        expect(restock).toBeEnabled();
        expect(sessionStorage.length).toBe(0);
    });

    it("prevents a second submission while the first movement is still in flight", async () => {
        useAuth.mockReturnValue({ user: { id: "admin-1", companyId: "company-1", role: "Admin" } });
        getInventorySlots.mockResolvedValue([{ id: "slot-1", machineId: "machine-1", machineName: "Máquina 1", itemNumber: 1, productId: "product-1", productName: "Água", capacity: 20, currentStock: 5, lowStockThreshold: 2, isLowStock: false }]);
        let finishRequest!: () => void;
        restockInventorySlot.mockImplementation(() => new Promise<void>((resolve) => { finishRequest = resolve; }));
        render(<MemoryRouter><Inventory /></MemoryRouter>);

        fireEvent.click(await screen.findByRole("button", { name: "Repor Água" }));
        fireEvent.change(screen.getByLabelText("Motivo"), { target: { value: "Reposição semanal" } });
        const submit = screen.getByRole("button", { name: "Registrar" });
        fireEvent.click(submit);
        fireEvent.click(submit);
        await waitFor(() => expect(restockInventorySlot).toHaveBeenCalledOnce());
        expect(screen.getByRole("button", { name: "Cancelar" })).toBeDisabled();
        const pending = sessionStorage.getItem(inventoryPendingKey("admin-1", "company-1"));
        expect(pending).toContain('"machineId":"machine-1"');
        expect(pending).not.toContain("Reposição semanal");
        finishRequest();
        await waitFor(() => expect(screen.queryByRole("button", { name: "Registrar" })).not.toBeInTheDocument());
        expect(sessionStorage.getItem(inventoryPendingKey("admin-1", "company-1"))).toBeNull();
    });

    it("keeps a pending warning scoped to the same user and company", async () => {
        sessionStorage.setItem(inventoryPendingKey("admin-1", "company-1"), JSON.stringify({ machineId: "machine-1", itemNumber: 1, movementType: "Restock", quantity: 1, startedAt: new Date().toISOString() }));
        getInventorySlots.mockResolvedValue([{ id: "slot-1", machineId: "machine-1", machineName: "Máquina 1", itemNumber: 1, productId: "product-1", productName: "Água", capacity: 20, currentStock: 5, lowStockThreshold: 2, isLowStock: false }]);
        useAuth.mockReturnValue({ user: { id: "admin-1", companyId: "company-2", role: "Admin" } });
        const { unmount } = render(<MemoryRouter><Inventory /></MemoryRouter>);
        expect(await screen.findByRole("button", { name: "Repor Água" })).toBeEnabled();
        expect(screen.queryByText(/Há uma movimentação sem confirmação nesta sessão/)).not.toBeInTheDocument();
        unmount();
        useAuth.mockReturnValue({ user: { id: "admin-2", companyId: "company-1", role: "Admin" } });
        const anotherUser = render(<MemoryRouter><Inventory /></MemoryRouter>);
        expect(await screen.findByRole("button", { name: "Repor Água" })).toBeEnabled();
        expect(screen.queryByText(/Há uma movimentação sem confirmação nesta sessão/)).not.toBeInTheDocument();
        anotherUser.unmount();
        useAuth.mockReturnValue({ user: { id: "admin-1", companyId: "company-1", role: "Admin" } });
        render(<MemoryRouter><Inventory /></MemoryRouter>);
        expect(await screen.findByText(/Há uma movimentação sem confirmação nesta sessão/)).toBeInTheDocument();
        expect(await screen.findByRole("button", { name: "Repor Água" })).toBeDisabled();
    });

    it("does not submit a movement if the browser cannot save its pending state", async () => {
        useAuth.mockReturnValue({ user: { id: "admin-1", companyId: "company-1", role: "Admin" } });
        getInventorySlots.mockResolvedValue([{ id: "slot-1", machineId: "machine-1", machineName: "Máquina 1", itemNumber: 1, productId: "product-1", productName: "Água", capacity: 20, currentStock: 5, lowStockThreshold: 2, isLowStock: false }]);
        render(<MemoryRouter><Inventory /></MemoryRouter>);
        fireEvent.click(await screen.findByRole("button", { name: "Repor Água" }));
        fireEvent.change(screen.getByLabelText("Motivo"), { target: { value: "Reposição semanal" } });
        const storageSpy = vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => { throw new Error("storage unavailable"); });
        try {
            fireEvent.click(screen.getByRole("button", { name: "Registrar" }));
            expect(restockInventorySlot).not.toHaveBeenCalled();
            expect(await screen.findByText(/Não foi possível verificar as movimentações pendentes desta sessão/)).toBeInTheDocument();
        } finally {
            storageSpy.mockRestore();
        }
    });

    it("blocks stock changes when the stored pending state is corrupted", async () => {
        sessionStorage.setItem(inventoryPendingKey("admin-1", "company-1"), "{invalid");
        useAuth.mockReturnValue({ user: { id: "admin-1", companyId: "company-1", role: "Admin" } });
        getInventorySlots.mockResolvedValue([{ id: "slot-1", machineId: "machine-1", machineName: "Máquina 1", itemNumber: 1, productId: "product-1", productName: "Água", capacity: 20, currentStock: 5, lowStockThreshold: 2, isLowStock: false }]);
        render(<MemoryRouter><Inventory /></MemoryRouter>);
        expect(await screen.findByText(/Não foi possível verificar as movimentações pendentes desta sessão/)).toBeInTheDocument();
        expect(await screen.findByRole("button", { name: "Repor Água" })).toBeDisabled();
        expect(restockInventorySlot).not.toHaveBeenCalled();
    });

    it("uses the selected company when checking a platform administrator's pending movement", async () => {
        sessionStorage.setItem(inventoryPendingKey("platform-1", "company-1"), JSON.stringify({ machineId: "machine-1", itemNumber: 1, movementType: "Restock", quantity: 1, startedAt: new Date().toISOString() }));
        useAuth.mockReturnValue({ user: { id: "platform-1", companyId: "platform-company", role: "PlatformAdmin" } });
        useImpersonation.mockReturnValue({ isSuperAdmin: true, impersonatedCompany: { id: "company-1", name: "Empresa A" } });
        getInventorySlots.mockResolvedValue([{ id: "slot-1", machineId: "machine-1", machineName: "Máquina 1", itemNumber: 1, productId: "product-1", productName: "Água", capacity: 20, currentStock: 5, lowStockThreshold: 2, isLowStock: false }]);
        render(<MemoryRouter><Inventory /></MemoryRouter>);
        expect(await screen.findByText(/Há uma movimentação sem confirmação nesta sessão/)).toBeInTheDocument();
        expect(await screen.findByRole("button", { name: "Repor Água" })).toBeDisabled();
    });

    it("confirms only the saved request with matching movement and reason fingerprint", async () => {
        const requestId = "b9d0ce8b-e9c2-43c5-b8e2-4f4f3e5a1001";
        const reasonHash = "a".repeat(64);
        sessionStorage.setItem(inventoryPendingKey("admin-1", "company-1"), JSON.stringify({ machineId: "machine-1", itemNumber: 1, movementType: "Restock", quantity: 3, startedAt: new Date().toISOString(), requestId, reasonHash }));
        useAuth.mockReturnValue({ user: { id: "admin-1", companyId: "company-1", role: "Admin" } });
        getInventorySlots.mockResolvedValue([{ id: "slot-1", machineId: "machine-1", machineName: "Máquina 1", itemNumber: 1, productId: "product-1", productName: "Água", capacity: 20, currentStock: 5, lowStockThreshold: 2, isLowStock: false }]);
        getInventoryMovementReceipt
            .mockResolvedValueOnce(null)
            .mockResolvedValueOnce({ machineId: "machine-1", itemNumber: 1, movementType: "Restock", quantityDelta: 3, reasonHash: "b".repeat(64) })
            .mockResolvedValueOnce({ machineId: "machine-1", itemNumber: 1, movementType: "Restock", quantityDelta: 3, reasonHash });
        render(<MemoryRouter><Inventory /></MemoryRouter>);

        const check = await screen.findByRole("button", { name: "Consultar tentativa" });
        fireEvent.click(check);
        expect(await screen.findByText(/Não foi possível confirmar esta tentativa/)).toBeInTheDocument();
        await waitFor(() => expect(check).toBeEnabled());
        expect(sessionStorage.getItem(inventoryPendingKey("admin-1", "company-1"))).not.toBeNull();
        fireEvent.click(check);
        await waitFor(() => expect(getInventoryMovementReceipt).toHaveBeenCalledTimes(2));
        await waitFor(() => expect(check).toBeEnabled());
        expect(screen.getByText(/Não foi possível confirmar esta tentativa/)).toBeInTheDocument();
        expect(sessionStorage.getItem(inventoryPendingKey("admin-1", "company-1"))).not.toBeNull();
        fireEvent.click(check);
        await waitFor(() => expect(sessionStorage.getItem(inventoryPendingKey("admin-1", "company-1"))).toBeNull());
        expect(getInventoryMovementReceipt).toHaveBeenCalledTimes(3);
        expect(getInventoryMovementReceipt).toHaveBeenCalledWith(requestId);
    });

    it("clears the pending marker after a known rejection and allows correction", async () => {
        useAuth.mockReturnValue({ user: { id: "admin-1", companyId: "company-1", role: "Admin" } });
        getInventorySlots.mockResolvedValue([{ id: "slot-1", machineId: "machine-1", machineName: "Máquina 1", itemNumber: 1, productId: "product-1", productName: "Água", capacity: 20, currentStock: 5, lowStockThreshold: 2, isLowStock: false }]);
        restockInventorySlot.mockRejectedValue(new Error("Sessão MDB ativa."));
        render(<MemoryRouter><Inventory /></MemoryRouter>);
        fireEvent.click(await screen.findByRole("button", { name: "Repor Água" }));
        fireEvent.change(screen.getByLabelText("Motivo"), { target: { value: "Reposição semanal" } });
        fireEvent.click(screen.getByRole("button", { name: "Registrar" }));
        await waitFor(() => expect(restockInventorySlot).toHaveBeenCalledOnce());
        await waitFor(() => expect(screen.getByRole("button", { name: "Registrar" })).toBeEnabled());
        expect(sessionStorage.getItem(inventoryPendingKey("admin-1", "company-1"))).toBeNull();
        expect(screen.queryByText(/Há uma movimentação sem confirmação nesta sessão/)).not.toBeInTheDocument();
    });
});
