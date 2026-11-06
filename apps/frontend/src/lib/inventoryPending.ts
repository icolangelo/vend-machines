export interface PendingInventoryMovement {
    machineId: string;
    itemNumber: number;
    movementType: "Restock" | "Adjustment";
    quantity: number;
    startedAt: string;
    requestId?: string;
    reasonHash?: string;
}

export async function hashInventoryReason(reason: string): Promise<string | undefined> {
    if (!globalThis.crypto?.subtle) return undefined;
    try {
        const digest = await globalThis.crypto.subtle.digest("SHA-256", new TextEncoder().encode(reason));
        return Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, "0")).join("");
    } catch {
        return undefined;
    }
}

export function inventoryPendingKey(userId: string, companyId: string): string {
    return `inventory-pending:${encodeURIComponent(userId)}:${encodeURIComponent(companyId)}`;
}

export function readPendingInventoryMovement(key: string): PendingInventoryMovement | null {
    const raw = sessionStorage.getItem(key);
    if (raw === null) return null;
    const value: unknown = JSON.parse(raw);
    if (!value || typeof value !== "object") throw new Error("Estado de movimentação inválido.");
    const item = value as Partial<PendingInventoryMovement>;
    if (typeof item.machineId !== "string" || !item.machineId ||
        !Number.isInteger(item.itemNumber) || (item.itemNumber ?? -1) < 0 ||
        (item.movementType !== "Restock" && item.movementType !== "Adjustment") ||
        !Number.isInteger(item.quantity) || item.quantity === 0 ||
        typeof item.startedAt !== "string" || !Number.isFinite(Date.parse(item.startedAt)) ||
        (item.requestId !== undefined && (typeof item.requestId !== "string" || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(item.requestId))) ||
        (item.reasonHash !== undefined && (typeof item.reasonHash !== "string" || !/^[0-9a-f]{64}$/.test(item.reasonHash)))) {
        throw new Error("Estado de movimentação inválido.");
    }
    return item as PendingInventoryMovement;
}
