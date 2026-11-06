import {
    machines as mockMachines,
    clients as mockClients,
    products as mockProducts,
    productTypes as mockProductTypes,
    topProducts as mockTopProducts,
    bottomProducts as mockBottomProducts,
    secondaryCompanyMachines,
    secondaryCompanyClients,
    secondaryCompanyProducts,
    secondaryCompanyTopProducts,
    secondaryCompanyBottomProducts,
    generateHistoricalData as mockHistoricalData,
    type Machine,
    type Client,
    type Location,
    type FullProduct,
    type Product,
    type ProductType
} from "@/data/mockData";
import { API_BASE_URL, authenticatedFetch, getCurrentAuthUser, SessionExpiredError } from "@/lib/authClient";

export { API_BASE_URL } from "@/lib/authClient";



const authFetch = authenticatedFetch;
const TEST_COMPANY_ACME_ID = "11111111-1111-1111-1111-111111111111";
const TEST_COMPANY_SABOR_ID = "55555555-5555-5555-5555-555555555555";

function getTestCompanyId(): string {
    const user = getCurrentAuthUser();
    if (user?.role === "PlatformAdmin") {
        try {
            const stored = sessionStorage.getItem("impersonatedCompany");
            const impersonatedId = stored ? (JSON.parse(stored) as { id?: string }).id : null;
            if (impersonatedId) return impersonatedId;
        } catch {
            // Ignore invalid test-only impersonation state and use the signed-in company.
        }
    }
    return user?.companyId || TEST_COMPANY_ACME_ID;
}

function getTestMachines(): Machine[] {
    const companyId = getTestCompanyId();
    if (companyId === TEST_COMPANY_SABOR_ID) return secondaryCompanyMachines;
    if (companyId === TEST_COMPANY_ACME_ID) return mockMachines;
    return [];
}

function getTestClients(): Client[] {
    const companyId = getTestCompanyId();
    if (companyId === TEST_COMPANY_SABOR_ID) return secondaryCompanyClients;
    if (companyId === TEST_COMPANY_ACME_ID) return mockClients;
    return [];
}

function isSaborTestCompany(): boolean {
    return getTestCompanyId() === TEST_COMPANY_SABOR_ID;
}

function isGlobalTestPlatformAdmin(): boolean {
    if (getCurrentAuthUser()?.role !== "PlatformAdmin") return false;
    try {
        return !sessionStorage.getItem("impersonatedCompany");
    } catch {
        return false;
    }
}

function canModifyTestUser(userId: string): boolean {
    const currentUser = getCurrentAuthUser();
    if (!currentUser || (currentUser.role !== "Admin" && currentUser.role !== "PlatformAdmin")) return false;
    if (currentUser.id === userId) return false;
    const match = /^usr-(\d+)$/.exec(userId);
    if (!match) return false;
    const userNumber = Number(match[1]);
    if (!Number.isInteger(userNumber) || userNumber < 1 || userNumber > 45) return false;
    if (isGlobalTestPlatformAdmin()) return true;

    const companyId = (userNumber - 1) % 2 === 0 ? TEST_COMPANY_ACME_ID : TEST_COMPANY_SABOR_ID;
    return companyId === getTestCompanyId();
}

export async function getMachines(): Promise<Machine[]> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        return getTestMachines();
    }
    const response = await authFetch(`${API_BASE_URL}/machines`);
    if (!response.ok) throw new Error("Erro ao buscar máquinas da API");
    return response.json();
}

export interface TelemetrySessionSummary {
    sessionId: string;
    transactionId?: string | null;
    state: string;
    itemNumber?: number | null;
    amountCents?: number | null;
    closeReason?: string | null;
    selectionDeadlineAt?: string | null;
    paymentDeadlineAt?: string | null;
    deliveryDeadlineAt?: string | null;
    startedAt: string;
}

export interface TelemetryConnection {
    machineId: string;
    machineName: string;
    serialNumber: string;
    location: string;
    online: boolean;
    commandRouteAvailable: boolean;
    monitoringEnabled: boolean;
    autoOpenSessionEnabled: boolean;
    manualStartRequiresMdb: boolean;
    connectedAt?: string | null;
    disconnectedAt?: string | null;
    lastSeenAt?: string | null;
    mdbStatus?: string | null;
    mdbStatusUpdatedAt?: string | null;
    mdbStatusFresh: boolean;
    autoOpenLastAttemptAt?: string | null;
    autoOpenLastError?: string | null;
    activeSession?: TelemetrySessionSummary | null;
}

export interface TelemetryEventItem {
    id: string;
    machineId: string;
    sessionId?: string | null;
    transactionId?: string | null;
    eventType: string;
    createdAt: string;
}

export async function getTelemetryConnections(): Promise<TelemetryConnection[]> {
    const response = await authFetch(`${API_BASE_URL}/telemetry/connections?pageSize=200`);
    if (!response.ok) throw new Error("Erro ao carregar conexões de telemetria.");
    const result = await response.json();
    return result.items;
}

export async function setTelemetryMonitoring(machineId: string, enabled: boolean): Promise<void> {
    const response = await authFetch(`${API_BASE_URL}/telemetry/machines/${encodeURIComponent(machineId)}/monitoring`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ enabled })
    });
    if (!response.ok) {
        const error = await response.json().catch(() => ({}));
        throw new Error(error.message || "Não foi possível alterar o acompanhamento.");
    }
}

export async function startTelemetrySession(machineId: string): Promise<TelemetrySessionSummary> {
    const response = await authFetch(`${API_BASE_URL}/telemetry/machines/${encodeURIComponent(machineId)}/sessions`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ source: "panel" })
    });
    if (!response.ok) {
        const error = await response.json().catch(() => ({}));
        throw new Error(error.message || "Não foi possível abrir a sessão.");
    }
    return response.json();
}

export async function requestMdbStatus(machineId: string): Promise<{
    machineId: string;
    messageId: number;
    mdbStatus: string;
    mdbStatusUpdatedAt: string;
}> {
    const response = await authFetch(`${API_BASE_URL}/telemetry/machines/${encodeURIComponent(machineId)}/mdb-status`, {
        method: "POST",
    });
    if (!response.ok) {
        const error = await response.json().catch(() => ({}));
        throw new Error(error.message || "Não foi possível consultar o status MDB.");
    }
    return response.json();
}

export async function cancelTelemetrySession(sessionId: string, reason = "user_cancelled"): Promise<void> {
    const response = await authFetch(`${API_BASE_URL}/telemetry/sessions/${sessionId}/cancel`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ reason })
    });
    if (!response.ok) {
        const error = await response.json().catch(() => ({}));
        throw new Error(error.message || "Não foi possível encerrar a sessão.");
    }
}

export class TelemetryCommandRequestError extends Error {
    constructor(message: string, readonly code: string | null, readonly status: number) {
        super(message);
        this.name = "TelemetryCommandRequestError";
    }
}

export async function sendTelemetryCommand(machineId: string, command: string, requestId: string): Promise<void> {
    const response = await authFetch(`${API_BASE_URL}/telemetry/machines/${encodeURIComponent(machineId)}/commands`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ requestId, command })
    });
    if (!response.ok) {
        const error = await response.json().catch(() => ({} as { message?: string; code?: string }));
        throw new TelemetryCommandRequestError(
            error.message || "A máquina não confirmou o comando.",
            error.code ?? null,
            response.status
        );
    }
}

export async function getTelemetryEvents(machineId?: string): Promise<TelemetryEventItem[]> {
    const suffix = machineId ? `&machineId=${encodeURIComponent(machineId)}` : "";
    const response = await authFetch(`${API_BASE_URL}/telemetry/events?pageSize=100${suffix}`);
    if (!response.ok) throw new Error("Erro ao carregar eventos de telemetria.");
    const result = await response.json();
    return result.items;
}

export async function createTelemetrySocketTicket(): Promise<{ ticket: string; expiresAt: string }> {
    const response = await authFetch(`${API_BASE_URL}/telemetry/socket-ticket`, { method: "POST" });
    if (!response.ok) throw new Error("Não foi possível autenticar o canal em tempo real.");
    return response.json();
}

export async function getMachine(id: string): Promise<Machine | null> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        return getTestMachines().find(m => m.id === id) || null;
    }
    const response = await authFetch(`${API_BASE_URL}/machines/${id}`);
    if (!response.ok) return null;
    return response.json();
}

export async function createMachine(machine: Partial<Machine>): Promise<Machine> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        const machines = getTestMachines();
        const clients = getTestClients();
        const newId = isSaborTestCompany()
            ? `VM-${String(101 + machines.length).padStart(3, '0')}`
            : `VM-${String(machines.length + 1).padStart(3, '0')}`;
        const selectedLocation = clients.find(location => location.id === machine.locationId);
        const newMachine: Machine = {
            id: newId,
            name: machine.name || "",
            locationId: selectedLocation?.id,
            clientName: selectedLocation?.name || machine.clientName || "ACME - Shopping",
            location: machine.location || selectedLocation?.name || "ACME - Shopping",
            status: machine.status || "online",
            stockLevel: machine.stockLevel ?? 100,
            revenue30d: machine.revenue30d ?? 0,
            totalSales30d: machine.totalSales30d ?? 0,
            lastSync: "Agora",
            serialNumber: machine.serialNumber || "",
            autoOpenSessionEnabled: machine.autoOpenSessionEnabled ?? false,
        };
        machines.push(newMachine);
        return newMachine;
    }
    const response = await authFetch(`${API_BASE_URL}/machines`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(machine)
    });
    if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.message || "Erro ao criar máquina na API");
    }
    return response.json();
}

export async function updateMachine(id: string, machine: Partial<Machine>): Promise<void> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        const machines = getTestMachines();
        const clients = getTestClients();
        const idx = machines.findIndex(m => m.id === id);
        if (idx > -1) {
            const selectedLocation = clients.find(location => location.id === machine.locationId);
            machines[idx] = {
                ...machines[idx],
                ...machine,
                clientName: selectedLocation?.name || machine.clientName || machines[idx].clientName,
                location: machine.location || selectedLocation?.name || machines[idx].location
            } as Machine;
        }
        return;
    }
    const response = await authFetch(`${API_BASE_URL}/machines/${id}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(machine)
    });
    if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.message || "Erro ao atualizar máquina na API");
    }
}

export interface TelemetryCredentialRotation {
    machineId: string;
    credential: string;
    createdAt: string;
    message: string;
}

export async function rotateTelemetryCredential(id: string): Promise<TelemetryCredentialRotation> {
    const response = await authFetch(`${API_BASE_URL}/machines/${encodeURIComponent(id)}/telemetry-credential`, {
        method: "POST",
    });
    if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.message || "Não foi possível gerar a credencial da máquina.");
    }
    return response.json();
}

export async function revokeTelemetryCredential(id: string): Promise<void> {
    const response = await authFetch(`${API_BASE_URL}/machines/${encodeURIComponent(id)}/telemetry-credential`, {
        method: "DELETE",
    });
    if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.message || "Não foi possível revogar a credencial da máquina.");
    }
}

export async function getLocations(): Promise<Location[]> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        return getTestClients();
    }
    const response = await authFetch(`${API_BASE_URL}/locations`);
    if (!response.ok) throw new Error("Erro ao buscar localizações da API");
    return response.json();
}

export async function createLocation(name: string): Promise<Location> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        const clients = getTestClients();
        const location: Location = {
            id: `loc-${Date.now()}`,
            name,
            machineCount: 0,
            revenue30d: 0,
            totalSales30d: 0,
            createdAt: new Date().toISOString()
        };
        clients.push(location);
        return location;
    }
    const response = await authFetch(`${API_BASE_URL}/locations`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ name })
    });
    if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.message || "Erro ao criar localização.");
    }
    return response.json();
}

export async function updateLocation(id: string, name: string): Promise<void> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        const location = getTestClients().find(item => item.id === id);
        if (location) {
            location.name = name;
            location.updatedAt = new Date().toISOString();
            getTestMachines()
                .filter(machine => machine.locationId === id)
                .forEach(machine => {
                    machine.clientName = name;
                    machine.location = name;
                });
        }
        return;
    }
    const response = await authFetch(`${API_BASE_URL}/locations/${id}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ name })
    });
    if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.message || "Erro ao atualizar localização.");
    }
}

export async function deleteLocation(id: string): Promise<void> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        const hasMachines = getTestMachines().some(machine => machine.locationId === id);
        if (hasMachines) {
            throw new Error("Não é possível excluir uma localização com máquinas vinculadas.");
        }
        const clients = getTestClients();
        const index = clients.findIndex(item => item.id === id);
        if (index >= 0) {
            clients.splice(index, 1);
        }
        return;
    }
    const response = await authFetch(`${API_BASE_URL}/locations/${id}`, {
        method: "DELETE"
    });
    if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.message || "Erro ao excluir localização.");
    }
}

export async function getClients(): Promise<Client[]> {
    return getLocations();
}

export async function getProducts(): Promise<FullProduct[]> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        return isSaborTestCompany() ? secondaryCompanyProducts : mockProducts;
    }
    const response = await authFetch(`${API_BASE_URL}/products`);
    if (!response.ok) throw new Error("Erro ao buscar produtos da API");
    return response.json();
}

export async function getProductTypes(): Promise<ProductType[]> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        return mockProductTypes;
    }
    const response = await authFetch(`${API_BASE_URL}/products/types`);
    if (!response.ok) throw new Error("Erro ao buscar tipos de produtos da API");
    return response.json();
}

export async function createProductType(type: Partial<ProductType>): Promise<ProductType> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) return { ...type, id: `pt-${Date.now()}` } as ProductType;
    const response = await authFetch(`${API_BASE_URL}/products/types`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(type)
    });
    if (!response.ok) throw new Error("Erro ao criar tipo de produto");
    return response.json();
}

export async function updateProductType(id: string, type: Partial<ProductType>): Promise<ProductType> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) return { ...type, id } as ProductType;
    const response = await authFetch(`${API_BASE_URL}/products/types/${id}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(type)
    });
    if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.message || "Erro ao atualizar tipo de produto");
    }
    return response.json();
}

export async function deleteProductType(id: string): Promise<void> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) return;
    const response = await authFetch(`${API_BASE_URL}/products/types/${id}`, {
        method: "DELETE"
    });
    if (!response.ok) throw new Error("Erro ao excluir tipo de produto");
}

export async function createProduct(product: Partial<FullProduct>): Promise<FullProduct> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) return { ...product, id: `p-${Date.now()}` } as FullProduct;
    const response = await authFetch(`${API_BASE_URL}/products`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(product)
    });
    if (!response.ok) throw new Error("Erro ao criar produto");
    return response.json();
}

export async function updateProduct(id: string, product: Partial<FullProduct>): Promise<FullProduct> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) return { ...product, id } as FullProduct;
    const response = await authFetch(`${API_BASE_URL}/products/${id}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(product)
    });
    if (!response.ok) throw new Error("Erro ao atualizar produto");
    return response.json();
}

export async function deleteProduct(id: string): Promise<void> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) return;
    const response = await authFetch(`${API_BASE_URL}/products/${id}`, {
        method: "DELETE"
    });
    if (!response.ok) throw new Error("Erro ao excluir produto");
}

export async function getTopProducts(): Promise<Product[]> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        return isSaborTestCompany() ? secondaryCompanyTopProducts : mockTopProducts;
    }
    const response = await authFetch(`${API_BASE_URL}/products/top`);
    if (!response.ok) throw new Error("Erro ao buscar mais vendidos da API");
    return response.json();
}

export async function getBottomProducts(): Promise<Product[]> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        return isSaborTestCompany() ? secondaryCompanyBottomProducts : mockBottomProducts;
    }
    const response = await authFetch(`${API_BASE_URL}/products/bottom`);
    if (!response.ok) throw new Error("Erro ao buscar menos vendidos da API");
    return response.json();
}

export interface InventorySlot {
    id: string;
    machineId: string;
    machineName?: string | null;
    itemNumber: number;
    productId: string;
    productName?: string | null;
    capacity: number;
    currentStock: number;
    lowStockThreshold: number;
    isLowStock: boolean;
    createdAt: string;
    updatedAt: string;
}

export interface InventoryMovementItem {
    id: string;
    machineId: string;
    itemNumber: number;
    productId: string;
    productName?: string | null;
    movementType: string;
    quantityDelta: number;
    quantityAfter: number;
    reason: string;
    userId?: string | null;
    createdAt: string;
}

export interface InventoryMovementPage {
    items: InventoryMovementItem[];
    page: number;
    pageSize: number;
    totalItems: number;
    totalPages: number;
}

export interface OperationalAlertItem {
    id: string;
    machineId?: string | null;
    machineName?: string | null;
    clientName?: string | null;
    sessionId?: string | null;
    sessionState?: string | null;
    transactionId?: string | null;
    type: string;
    severity: "warning" | "critical";
    status: "Active" | "Resolved";
    title: string;
    description: string;
    occurredAt: string;
    updatedAt: string;
    resolvedAt?: string | null;
    resolution?: string | null;
    isRead: boolean;
}

export interface OperationalAlertSummary {
    disconnectedMachines: number;
    otherActiveAlerts: number;
}

const TEST_CLOSURE_ALERT_SESSION_ID = "test-session-closure";
const TEST_CLOSURE_ALERT_STATE_KEY = "vm-test-closure-alert-evidence";

function getTestClosureAlert(): OperationalAlertItem | null {
    const user = getCurrentAuthUser();
    if (!user || user.role === "Operator") return null;

    const evidenceReference = sessionStorage.getItem(TEST_CLOSURE_ALERT_STATE_KEY);
    const resolved = evidenceReference !== null;
    return {
        id: "test-closure-alert",
        machineId: "VM-CLOSURE",
        machineName: "Máquina de teste",
        clientName: user.companyName ?? "ACME - Hospital",
        sessionId: TEST_CLOSURE_ALERT_SESSION_ID,
        sessionState: resolved ? "ClosureVerified" : "ClosureIncomplete",
        type: "SessionFailure",
        severity: "critical",
        status: resolved ? "Resolved" : "Active",
        title: "Encerramento físico pendente",
        description: "A sessão aguarda confirmação de que a máquina está ociosa.",
        occurredAt: "2026-10-26T12:00:00Z",
        updatedAt: "2026-10-26T12:00:00Z",
        resolvedAt: resolved ? "2026-10-26T12:05:00Z" : null,
        resolution: resolved
            ? `Encerramento físico confirmado por responsável autorizado. Referência de evidência: ${evidenceReference}.`
            : null,
        isRead: true,
    };
}

export async function getOperationalAlertSummary(): Promise<OperationalAlertSummary> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) return { disconnectedMachines: 0, otherActiveAlerts: 0 };
    const response = await authFetch(`${API_BASE_URL}/alerts/summary`);
    if (!response.ok) throw new Error(await readApiError(response, "Erro ao buscar o resumo de alertas."));
    return response.json();
}

export interface OperationalAlertPage {
    items: OperationalAlertItem[];
    total: number;
    page: number;
    pageSize: number;
}

export async function getOperationalAlerts(
    status: "Active" | "Resolved" | "All" = "Active",
    page = 1,
    pageSize = 50,
    type: "TelemetryOffline" | "OtherActive" | "All" = "All",
    search = "",
): Promise<OperationalAlertPage> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        const closureAlert = getTestClosureAlert();
        const matchesStatus = closureAlert && (status === "All" || closureAlert.status === status);
        const matchesType = closureAlert && (type === "All" || (type === "OtherActive" && closureAlert.status === "Active"));
        const matchesSearch = closureAlert && (!search.trim() ||
            `${closureAlert.title} ${closureAlert.description} ${closureAlert.machineId} ${closureAlert.machineName}`
                .toLocaleLowerCase("pt-BR")
                .includes(search.trim().toLocaleLowerCase("pt-BR")));
        const items = matchesStatus && matchesType && matchesSearch ? [closureAlert] : [];
        return { items, total: items.length, page, pageSize };
    }
    const params = new URLSearchParams({ status, page: String(page), pageSize: String(pageSize), type, search });
    const response = await authFetch(`${API_BASE_URL}/alerts?${params.toString()}`);
    if (!response.ok) throw new Error(await readApiError(response, "Erro ao buscar alertas."));
    return response.json();
}

export async function markOperationalAlertRead(id: string): Promise<void> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) return;
    const response = await authFetch(`${API_BASE_URL}/alerts/${encodeURIComponent(id)}/read`, { method: "PUT" });
    if (!response.ok) throw new Error(await readApiError(response, "Não foi possível marcar o alerta como lido."));
}

export async function resolveIncompleteSessionClosure(
    sessionId: string,
    evidenceReference: string,
): Promise<void> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        if (sessionId !== TEST_CLOSURE_ALERT_SESSION_ID || getCurrentAuthUser()?.role === "Operator") {
            throw new Error("Sessão não encontrada ou usuário sem permissão para confirmar o encerramento.");
        }
        if (!/^[A-Za-z0-9._/:_-]{3,64}$/.test(evidenceReference)) {
            throw new Error("Informe uma referência de evidência válida.");
        }
        sessionStorage.setItem(TEST_CLOSURE_ALERT_STATE_KEY, evidenceReference);
        return;
    }
    const response = await authFetch(`${API_BASE_URL}/telemetry/sessions/${encodeURIComponent(sessionId)}/resolve-incomplete-closure`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ confirmedMachineIdle: true, evidenceReference }),
    });
    if (!response.ok) throw new Error(await readApiError(response, "Não foi possível confirmar o encerramento da sessão."));
}

export interface SalesReportRow {
    id: string;
    mercadoPagoPaymentId?: string | null;
    soldAt: string;
    machineId: string;
    machineName: string;
    clientName: string;
    location: string;
    itemNumber: number;
    productName: string;
    quantity: number;
    grossAmount: number;
    refundedAmount: number;
    netRevenue: number | null;
    applicationFee: number;
    netAfterPlatformFee: number;
    financialStatus: string;
}

export interface SalesReportResult {
    from: string;
    to: string;
    total: number;
    truncated: boolean;
    items: SalesReportRow[];
}

export interface SalesReportFilters {
    machineId?: string;
    locationId?: string;
    productId?: string;
}

function buildSalesReportQuery(from: string, to: string, filters: SalesReportFilters = {}): string {
    const query = new URLSearchParams({ from, to });
    if (filters.machineId) query.set("machineId", filters.machineId);
    if (filters.locationId) query.set("locationId", filters.locationId);
    if (filters.productId) query.set("productId", filters.productId);
    return query.toString();
}

export async function getSalesReport(from: string, to: string, filters: SalesReportFilters = {}): Promise<SalesReportResult> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) return { from, to, total: 0, truncated: false, items: [] };
    const response = await authFetch(`${API_BASE_URL}/reports/sales?${buildSalesReportQuery(from, to, filters)}`);
    if (!response.ok) throw new Error(await readApiError(response, "Erro ao gerar relatório de vendas."));
    return response.json();
}

export async function exportSalesReport(from: string, to: string, filters: SalesReportFilters = {}): Promise<Blob> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) return new Blob(["Id;Data UTC\n"], { type: "text/csv;charset=utf-8" });
    const response = await authFetch(`${API_BASE_URL}/reports/sales/export?${buildSalesReportQuery(from, to, filters)}`);
    if (!response.ok) throw new Error(await readApiError(response, "Erro ao exportar relatório de vendas."));
    return response.blob();
}

export async function getInventorySlots(machineId?: string): Promise<InventorySlot[]> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) return [];
    const query = machineId ? `?machineId=${encodeURIComponent(machineId)}` : "";
    const response = await authFetch(`${API_BASE_URL}/inventory/slots${query}`);
    if (!response.ok) throw new Error("Erro ao buscar compartimentos do estoque.");
    return response.json();
}

export async function configureInventorySlot(
    machineId: string,
    itemNumber: number,
    data: { productId: string; capacity: number; initialStock: number; lowStockThreshold: number }
): Promise<void> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) return;
    const response = await authFetch(`${API_BASE_URL}/inventory/slots/${encodeURIComponent(machineId)}/${itemNumber}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(data)
    });
    if (!response.ok) throw new Error(await readApiError(response, "Erro ao configurar compartimento."));
}

export class InventoryMovementUncertainError extends Error {
    constructor() {
        super("O resultado da movimentação não pôde ser confirmado.");
        this.name = "InventoryMovementUncertainError";
    }
}

export interface InventoryMovementReceipt {
    machineId: string;
    itemNumber: number;
    movementType: "Restock" | "Adjustment";
    quantityDelta: number;
    reasonHash: string;
}

export async function getInventoryMovementReceipt(requestId: string): Promise<InventoryMovementReceipt | null> {
    if (import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true") return null;
    const response = await authFetch(`${API_BASE_URL}/inventory/movements/by-request/${encodeURIComponent(requestId)}`);
    if (response.status === 404) return null;
    if (!response.ok) throw new Error("Não foi possível consultar a movimentação.");
    const value: unknown = await response.json();
    if (!value || typeof value !== "object") throw new Error("Resposta de movimentação inválida.");
    const receipt = value as Partial<InventoryMovementReceipt>;
    if (typeof receipt.machineId !== "string" || !Number.isInteger(receipt.itemNumber) ||
        (receipt.movementType !== "Restock" && receipt.movementType !== "Adjustment") ||
        !Number.isInteger(receipt.quantityDelta) || typeof receipt.reasonHash !== "string" ||
        !/^[0-9a-f]{64}$/.test(receipt.reasonHash)) throw new Error("Resposta de movimentação inválida.");
    return receipt as InventoryMovementReceipt;
}

async function sendInventoryMovement(url: string, body: object, fallback: string): Promise<void> {
    let response: Response;
    try {
        response = await authFetch(url, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(body),
        });
    } catch (error) {
        if (error instanceof SessionExpiredError) throw error;
        throw new InventoryMovementUncertainError();
    }
    if (response.status === 408 || response.status >= 500) throw new InventoryMovementUncertainError();
    if (!response.ok) throw new Error(await readApiError(response, fallback));
}

export async function restockInventorySlot(
    machineId: string,
    itemNumber: number,
    quantity: number,
    reason: string,
    requestId?: string
): Promise<void> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) return;
    await sendInventoryMovement(`${API_BASE_URL}/inventory/slots/${encodeURIComponent(machineId)}/${itemNumber}/restock`,
        { quantity, reason, requestId }, "Erro ao registrar reposição.");
}

export async function adjustInventorySlot(
    machineId: string,
    itemNumber: number,
    quantityDelta: number,
    reason: string,
    requestId?: string
): Promise<void> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) return;
    await sendInventoryMovement(`${API_BASE_URL}/inventory/slots/${encodeURIComponent(machineId)}/${itemNumber}/adjustment`,
        { quantityDelta, reason, requestId }, "Erro ao ajustar estoque.");
}

export async function getInventoryMovements(
    page = 1,
    pageSize = 50,
    filters: { machineId?: string; itemNumber?: number } = {}
): Promise<InventoryMovementPage> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) return { items: [], page, pageSize, totalItems: 0, totalPages: 0 };
    const query = new URLSearchParams({ page: String(page), pageSize: String(pageSize) });
    if (filters.machineId) query.set("machineId", filters.machineId);
    if (filters.itemNumber !== undefined) query.set("itemNumber", String(filters.itemNumber));
    const response = await authFetch(`${API_BASE_URL}/inventory/movements?${query}`);
    if (!response.ok) throw new Error("Erro ao buscar histórico de estoque.");
    return response.json();
}

export async function exportInventoryMovements(
    filters: { machineId?: string; itemNumber?: number } = {}
): Promise<Blob> {
    if (import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")
        return new Blob(["Data UTC;ID da máquina;Compartimento;Produto;Tipo;Variação;Saldo;Motivo\r\n"], { type: "text/csv;charset=utf-8" });
    const query = new URLSearchParams();
    if (filters.machineId) query.set("machineId", filters.machineId);
    if (filters.itemNumber !== undefined) query.set("itemNumber", String(filters.itemNumber));
    const response = await authFetch(`${API_BASE_URL}/inventory/movements/export?${query}`);
    if (!response.ok) throw new Error(await readApiError(response, "Erro ao exportar histórico de estoque."));
    return response.blob();
}

async function readApiError(response: Response, fallback: string): Promise<string> {
    try {
        const payload = await response.json();
        return payload.message || fallback;
    } catch {
        return fallback;
    }
}

export interface DashboardStats {
    totalRevenue30d: number;
    totalSales30d: number;
    onlineMachines: number;
    warningMachines: number;
    offlineMachines: number;
    revenueHistory: { day: string; revenue: number }[];
    performanceHistory: { date: string; revenue: number; products: number }[];
}

export async function getDashboardStats(): Promise<DashboardStats> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        const machines = getTestMachines();
        const totalRevenue = machines.reduce((s, m) => s + m.revenue30d, 0);
        const totalSales = machines.reduce((s, m) => s + m.totalSales30d, 0);
        const online = machines.filter((m) => m.status === "online").length;
        const warning = machines.filter((m) => m.status === "warning").length;
        const offline = machines.filter((m) => m.status === "offline").length;
        const revenueHistory = isSaborTestCompany()
            ? [
                { day: "Seg", revenue: 410 },
                { day: "Ter", revenue: 455 },
                { day: "Qua", revenue: 438 },
                { day: "Qui", revenue: 492 },
                { day: "Sex", revenue: 517 },
                { day: "Sáb", revenue: 560 },
                { day: "Dom", revenue: 448 },
            ]
            : [
                { day: "Seg", revenue: 2840 },
                { day: "Ter", revenue: 3120 },
                { day: "Qua", revenue: 2960 },
                { day: "Qui", revenue: 3450 },
                { day: "Sex", revenue: 3890 },
                { day: "Sáb", revenue: 4210 },
                { day: "Dom", revenue: 2180 }
            ];
        const performanceHistory = isSaborTestCompany()
            ? Array.from({ length: 30 }, (_, index) => {
                const date = new Date();
                date.setDate(date.getDate() - (29 - index));
                return {
                    date: date.toLocaleDateString("pt-BR", { day: "2-digit", month: "2-digit" }),
                    revenue: Math.round(totalRevenue / 30),
                    products: Math.round(totalSales / 30),
                };
            })
            : mockHistoricalData(30);

        return {
            totalRevenue30d: totalRevenue,
            totalSales30d: totalSales,
            onlineMachines: online,
            warningMachines: warning,
            offlineMachines: offline,
            revenueHistory,
            performanceHistory
        };
    }
    const response = await authFetch(`${API_BASE_URL}/dashboard/stats`);
    if (!response.ok) throw new Error("Erro ao buscar estatísticas do painel da API");
    return response.json();
}

export interface PaginatedUsers {
    items: {
        id: string;
        name: string;
        email: string;
        role: string;
        isActive: boolean;
        cpf?: string;
        companyId?: string | null;
        companyName?: string | null;
        createdAt: string;
    }[];
    pageNumber: number;
    pageSize: number;
    totalItems: number;
    totalPages: number;
}

const mockUserRoleOverrides = new Map<string, string>();
const mockUserActiveOverrides = new Map<string, boolean>();

export async function getUsers(pageNumber = 1, pageSize = 10, searchTerm = ""): Promise<PaginatedUsers> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        const allMockUsers = Array.from({ length: 45 }, (_, i) => ({
            id: `usr-${i + 1}`,
            name: i === 0 ? "Admin Ivan" : i === 1 ? "João da Silva" : `Usuário Operador ${i + 1}`,
            email: i === 0 ? "dev.ivan@gmail.com" : i === 1 ? "joao@vendmachine.com.br" : `operador${i + 1}@vendmachine.com.br`,
            role: mockUserRoleOverrides.get(`usr-${i + 1}`) ?? (i === 0 ? "Admin" : "Operator"),
            isActive: mockUserActiveOverrides.get(`usr-${i + 1}`) ?? true,
            cpf: i === 0 ? "123.456.789-00" : i === 1 ? "987.654.321-99" : `000.000.000-${String(i).padStart(2, '0')}`,
            companyId: i % 2 === 0 ? "11111111-1111-1111-1111-111111111111" : "55555555-5555-5555-5555-555555555555",
            companyName: i % 2 === 0 ? "ACME Machines LTDA" : "Sabor & Cia Vending",
            createdAt: new Date(Date.now() - i * 24 * 60 * 60 * 1000).toISOString()
        }));

        let filtered = isGlobalTestPlatformAdmin()
            ? allMockUsers
            : allMockUsers.filter(user => user.companyId === getTestCompanyId());
        if (searchTerm) {
            const search = searchTerm.toLowerCase();
            filtered = filtered.filter(u => u.name.toLowerCase().includes(search) || u.email.toLowerCase().includes(search));
        }

        const totalItems = filtered.length;
        const totalPages = Math.ceil(totalItems / pageSize);
        const startIndex = (pageNumber - 1) * pageSize;
        const items = filtered.slice(startIndex, startIndex + pageSize);

        return {
            items,
            pageNumber,
            pageSize,
            totalItems,
            totalPages
        };
    }

    const response = await authFetch(`${API_BASE_URL}/users?pageNumber=${pageNumber}&pageSize=${pageSize}&searchTerm=${encodeURIComponent(searchTerm)}`);
    if (!response.ok) throw new Error("Erro ao buscar usuários");
    return response.json();
}

export async function updateUserRole(userId: string, role: "Admin" | "PlatformAdmin" | "Operator"): Promise<void> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        if (!canModifyTestUser(userId) || (role === "PlatformAdmin" && getCurrentAuthUser()?.role !== "PlatformAdmin")) {
            throw new Error("Usuário não encontrado no escopo permitido.");
        }
        mockUserRoleOverrides.set(userId, role);
        return;
    }

    const response = await authFetch(`${API_BASE_URL}/users/${encodeURIComponent(userId)}/role`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ role }),
    });
    if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.message || "Não foi possível alterar o papel do usuário.");
    }
}

export async function updateUserActiveStatus(userId: string, isActive: boolean): Promise<void> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        if (!canModifyTestUser(userId)) {
            throw new Error("Usuário não encontrado no escopo permitido.");
        }
        mockUserActiveOverrides.set(userId, isActive);
        return;
    }

    const response = await authFetch(`${API_BASE_URL}/users/${encodeURIComponent(userId)}/active`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ isActive }),
    });
    if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.message || "Não foi possível atualizar o status do usuário.");
    }
}

export async function createUserInvitation(email: string, role: "Admin" | "Operator"): Promise<{
    invitationId: string;
    invitationToken: string | null;
    emailSent: boolean;
    expiresAt: string;
    rotated: boolean;
}> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        return {
            invitationId: crypto.randomUUID(),
            invitationToken: `mock-invitation-${crypto.randomUUID()}`,
            emailSent: false,
            expiresAt: new Date(Date.now() + 7 * 24 * 60 * 60 * 1000).toISOString(),
            rotated: false,
        };
    }

    const response = await authFetch(`${API_BASE_URL}/users/invitations`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ email, role }),
    });
    if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.message || "Não foi possível criar o convite.");
    }
    return response.json();
}

export async function revokeUserInvitation(invitationId: string): Promise<void> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) return;
    const response = await authFetch(`${API_BASE_URL}/users/invitations/${encodeURIComponent(invitationId)}/revoke`, {
        method: "PUT",
    });
    if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.message || "Não foi possível revogar o convite.");
    }
}

export async function createPasswordResetLink(userId: string): Promise<{ resetToken: string | null; emailSent: boolean; expiresAt: string }> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        return { resetToken: `mock-reset-${crypto.randomUUID()}`, emailSent: false, expiresAt: new Date(Date.now() + 60 * 60 * 1000).toISOString() };
    }
    const response = await authFetch(`${API_BASE_URL}/users/${encodeURIComponent(userId)}/password-reset`, { method: "POST" });
    if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.message || "Não foi possível gerar o link de recuperação.");
    }
    return response.json();
}

export async function resetPassword(resetToken: string, password: string): Promise<void> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) return;
    const response = await fetch(`${API_BASE_URL}/auth/reset-password`, {
        method: "POST",
        credentials: "include",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ resetToken, password }),
    });
    if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.message || "Não foi possível redefinir a senha.");
    }
}

export interface EmailVerificationResult {
    emailVerified: boolean;
    accountActive?: boolean;
    message: string;
}

export async function verifyEmail(token: string): Promise<EmailVerificationResult> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        return { emailVerified: true, accountActive: false, message: "E-mail confirmado. A empresa ainda aguarda análise da plataforma." };
    }
    const response = await fetch(`${API_BASE_URL}/auth/verify-email`, {
        method: "POST",
        credentials: "include",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ token }),
    });
    const result = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(result.message || "Não foi possível confirmar o e-mail.");
    return result;
}

export async function resendEmailVerification(email: string): Promise<{ message: string }> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        return { message: "Se houver um cadastro aguardando confirmação, enviaremos um novo link para o endereço informado." };
    }
    const response = await fetch(`${API_BASE_URL}/auth/resend-email-verification`, {
        method: "POST",
        credentials: "include",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ email }),
    });
    const result = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(result.message || "Não foi possível solicitar um novo link.");
    return result;
}

export interface PaginatedCompanies {
    items: {
        id: string;
        name: string;
        cnpj?: string;
        activationStatus: "Pending" | "Active" | "Rejected";
        createdBy: string;
        createdByEmail: string;
        partners: string[];
        createdAt: string;
        enabledIntegrations?: string[];
    }[];
    pageNumber: number;
    pageSize: number;
    totalItems: number;
    totalPages: number;
}

export async function getCompanies(pageNumber = 1, pageSize = 10, searchTerm = ""): Promise<PaginatedCompanies> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        const allMockCompanies = [
            {
                id: "11111111-1111-1111-1111-111111111111",
                name: "ACME Machines LTDA",
                cnpj: "12.345.678/0001-99",
                activationStatus: "Active" as const,
                createdBy: "Admin Ivan",
                createdByEmail: "admin@acme.example",
                partners: ["João da Silva", "Maria Santos"],
                createdAt: new Date(Date.now() - 30 * 24 * 60 * 60 * 1000).toISOString(),
                enabledIntegrations: ["Mercado Pago"]
            },
            {
                id: "55555555-5555-5555-5555-555555555555",
                name: "Sabor & Cia Vending",
                cnpj: "98.765.432/0001-00",
                activationStatus: "Active" as const,
                createdBy: "Eduardo Souza",
                createdByEmail: "eduardo@example.com",
                partners: ["Ana Julia", "Pedro Mendes"],
                createdAt: new Date(Date.now() - 15 * 24 * 60 * 60 * 1000).toISOString(),
                enabledIntegrations: []
            },
            {
                id: "99999999-9999-9999-9999-999999999999",
                name: "Express Café Vending",
                cnpj: "11.222.333/0001-44",
                activationStatus: "Pending" as const,
                createdBy: "Roberto Lima",
                createdByEmail: "roberto@example.com",
                partners: [],
                createdAt: new Date(Date.now() - 5 * 24 * 60 * 60 * 1000).toISOString(),
                enabledIntegrations: []
            }
        ];

        let filtered = allMockCompanies;
        if (searchTerm) {
            const search = searchTerm.toLowerCase();
            filtered = allMockCompanies.filter(c => c.name.toLowerCase().includes(search));
        }

        const totalItems = filtered.length;
        const totalPages = Math.ceil(totalItems / pageSize);
        const startIndex = (pageNumber - 1) * pageSize;
        const items = filtered.slice(startIndex, startIndex + pageSize);

        return {
            items,
            pageNumber,
            pageSize,
            totalItems,
            totalPages
        };
    }

    const response = await authFetch(`${API_BASE_URL}/companies?pageNumber=${pageNumber}&pageSize=${pageSize}&searchTerm=${encodeURIComponent(searchTerm)}`);
    if (!response.ok) throw new Error("Erro ao buscar empresas");
    return response.json();
}

export async function updateCompanyActivation(
    companyId: string,
    status: "Active" | "Rejected",
): Promise<{ companyId: string; activationStatus: string; affectedUsers: number }> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        return { companyId, activationStatus: status, affectedUsers: status === "Rejected" ? 1 : 0 };
    }

    const response = await authFetch(`${API_BASE_URL}/companies/${companyId}/activation`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ status }),
    });
    if (!response.ok) {
        const error = await response.json().catch(() => ({}));
        throw new Error(error.message || "Não foi possível atualizar a ativação da empresa.");
    }
    return response.json();
}

export interface SystemSettings {
    id?: string;
    applicationFeePercent: number;
    updatedAt?: string;
    isConfigured?: boolean;
    isValid?: boolean;
}

export interface MercadoPagoIntegration {
    id?: string;
    companyId: string;
    ownerName: string;
    ownerCpf: string;
    ownerEmail: string;
    ownerPhone: string;
    businessName: string;
    tradeName: string;
    cnpj: string;
    businessEmail: string;
    businessPhone: string;
    accessToken: string;
    hasAccessToken?: boolean;
    hasRefreshToken?: boolean;
    accessTokenExpiresAt?: string;
    publicKey: string;
    clientId?: string;
    clientSecret?: string;
    hasClientSecret?: boolean;
    mercadoPagoUserId?: string;
    mercadoPagoNickname?: string;
    mercadoPagoSiteId?: string;
    lastTokenValidationAt?: string;
    lastTokenValidationStatus?: string;
    isActive: boolean;
    createdAt?: string;
    updatedAt?: string;
}

export interface PaymentTransaction {
    id: string;
    machineId: string;
    machine?: Pick<Machine, "id" | "name" | "serialNumber">;
    amount: number;
    applicationFee: number;
    status: string;
    mercadoPagoPaymentId?: string;
    mercadoPagoStatus?: string;
    mercadoPagoStatusDetail?: string;
    createdAt: string;
    completedAt?: string;
}

export interface PaymentTransactionHistoryPage {
    items: PaymentTransaction[];
    total: number;
    page: number;
    pageSize: number;
    totalPages: number;
}

export interface TransactionTelemetryLog {
    id: string;
    transactionId: string;
    timestamp: string;
    logType: "Info" | "CommandSent" | "AckReceived" | "Error" | "RefundTriggered";
    message: string;
}

export interface TransactionTelemetryLogPage {
    items: TransactionTelemetryLog[];
    total: number;
    page: number;
    pageSize: number;
    totalPages: number;
}

export class PaymentRequestError extends Error {
    constructor(message: string, public readonly transactionId?: string) {
        super(message);
        this.name = "PaymentRequestError";
    }
}

export async function getGlobalSettings(): Promise<SystemSettings> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        const local = localStorage.getItem("mock_global_settings");
        const settings = local ? JSON.parse(local) as SystemSettings : { applicationFeePercent: 5.0 };
        return {
            ...settings,
            isConfigured: local !== null,
            isValid: local !== null && settings.applicationFeePercent > 0 && settings.applicationFeePercent < 100
        };
    }
    const response = await authFetch(`${API_BASE_URL}/payments/global-settings`);
    if (response.status === 409) {
        throw new Error("Há mais de uma configuração global de taxa. Solicite a revisão dos registros antes de continuar.");
    }
    if (!response.ok) throw new Error("Erro ao carregar configurações de pagamento globais.");
    return response.json();
}

export async function updateGlobalSettings(settings: SystemSettings, expected?: SystemSettings): Promise<void> {
    const expectedFee = expected?.isConfigured === true ? String(expected.applicationFeePercent) : expected?.isConfigured === false ? "missing" : undefined;
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        const current = localStorage.getItem("mock_global_settings");
        const currentFee = current ? String((JSON.parse(current) as SystemSettings).applicationFeePercent) : "missing";
        if (expectedFee !== undefined && expectedFee !== currentFee) throw new Error("A taxa global foi alterada. Atualize e confira o valor antes de salvar novamente.");
        localStorage.setItem("mock_global_settings", JSON.stringify({ ...settings, isConfigured: true, isValid: true }));
        return;
    }
    const response = await authFetch(`${API_BASE_URL}/payments/global-settings`, {
        method: "POST",
        headers: { "Content-Type": "application/json", ...(expectedFee !== undefined ? { "X-Expected-Platform-Fee": expectedFee } : {}) },
        body: JSON.stringify(settings)
    });
    if (!response.ok) throw new Error("Erro ao salvar taxa global.");
}

export async function getIntegration(): Promise<MercadoPagoIntegration> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        const local = localStorage.getItem("mock_mp_integration");
        const companyId = "11111111-1111-1111-1111-111111111111";
        return local ? JSON.parse(local) : {
            companyId,
            ownerName: "", ownerCpf: "", ownerEmail: "", ownerPhone: "",
            businessName: "", tradeName: "", cnpj: "", businessEmail: "", businessPhone: "",
            accessToken: "", publicKey: "", isActive: false
        };
    }
    const response = await authFetch(`${API_BASE_URL}/payments/integration`);
    if (!response.ok) throw new Error("Erro ao carregar integração do Mercado Pago.");
    return response.json();
}

export async function toggleIntegration(isActive: boolean): Promise<void> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        const local = localStorage.getItem("mock_mp_integration");
        if (local) {
            const parsed = JSON.parse(local);
            parsed.isActive = isActive;
            localStorage.setItem("mock_mp_integration", JSON.stringify(parsed));
        }
        return;
    }
    const response = await authFetch(`${API_BASE_URL}/payments/integration/toggle`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ isActive })
    });
    if (!response.ok) throw new Error("Erro ao alterar status da integração.");
}

export interface OauthConfig {
    clientId: string;
    redirectUri: string;
    state?: string;
}

export async function getOauthConfig(): Promise<OauthConfig> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        return {
            clientId: "7902965695113613",
            redirectUri: window.location.origin + "/"
        };
    }
    const response = await authFetch(`${API_BASE_URL}/payments/oauth/config`);
    if (!response.ok) throw new Error("Erro ao carregar configurações do OAuth.");
    return response.json();
}

export async function exchangeOauthCode(code: string, redirectUri: string, state?: string): Promise<MercadoPagoIntegration> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        const companyId = "11111111-1111-1111-1111-111111111111";
        const mockIntegration: MercadoPagoIntegration = {
            companyId,
            ownerName: "Admin Ivan (Mock)",
            ownerCpf: "123.456.789-00",
            ownerEmail: "dev.ivan@gmail.com",
            ownerPhone: "11999999999",
            businessName: "ACME Machines LTDA (Mock)",
            tradeName: "ACME Machines LTDA",
            cnpj: "12.345.678/0001-99",
            businessEmail: "dev.ivan@gmail.com",
            businessPhone: "11999999999",
            accessToken: "APP_USR-DUMMY-OAUTH-MOCKTOKEN123456",
            publicKey: "APP_USR-MOCKPUBKEY123456",
            clientId: "7902965695113613",
            clientSecret: "MOCKSECRET",
            isActive: true
        };
        localStorage.setItem("mock_mp_integration", JSON.stringify(mockIntegration));
        return mockIntegration;
    }
    const response = await authFetch(`${API_BASE_URL}/payments/oauth/callback`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ code, redirectUri, state })
    });
    if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.message || "Erro ao realizar o callback do Mercado Pago.");
    }
    return response.json();
}

export async function disconnectIntegration(): Promise<void> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        localStorage.removeItem("mock_mp_integration");
        return;
    }
    const response = await authFetch(`${API_BASE_URL}/payments/integration/disconnect`, {
        method: "POST",
        headers: { "Content-Type": "application/json" }
    });
    if (!response.ok) throw new Error("Erro ao desconectar conta.");
}

export async function getCompanyIntegration(companyId: string): Promise<MercadoPagoIntegration> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        const local = localStorage.getItem("mock_mp_integration");
        if (local) {
            const parsed = JSON.parse(local);
            if (parsed.companyId === companyId) {
                return parsed;
            }
        }
        return {
            companyId,
            ownerName: "João da Silva",
            ownerCpf: "123.456.789-00",
            ownerEmail: "joao@acmemachines.com",
            ownerPhone: "(11) 98765-4321",
            businessName: "ACME Machines LTDA",
            tradeName: "ACME Vending",
            cnpj: "12.345.678/0001-99",
            businessEmail: "financeiro@acmemachines.com",
            businessPhone: "(11) 5555-1234",
            accessToken: "APP_USR-1234••••••••ABCD",
            hasAccessToken: true,
            publicKey: "APP_USR-9876••••••••XYZ",
            clientId: "acme_client_id",
            clientSecret: "acme_••••••••secret",
            hasClientSecret: true,
            isActive: true
        };
    }
    const response = await authFetch(`${API_BASE_URL}/companies/${companyId}/integration`);
    if (!response.ok) throw new Error("Erro ao carregar detalhes da integração da empresa.");
    return response.json();
}

export async function createPixQrCode(params: {
    machineId: string;
    amount: number;
    description?: string;
    payerEmail?: string;
    payerFirstName?: string;
    payerLastName?: string;
    payerCpf?: string;
    useRealMercadoPago?: boolean;
    sendTelemetryToMachine?: boolean;
}): Promise<{
    transactionId: string;
    qrCode: string;
    qrCodeBase64: string;
    status: string;
    applicationFee?: number;
    applicationFeeApplied?: boolean;
    applicationFeeWarning?: string | null;
    simulated?: boolean;
}> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        const mockTxId = `tx-${Date.now()}`;
        const mockTransactions = JSON.parse(localStorage.getItem("mock_transactions") || "[]");
        const newTx = {
            id: mockTxId,
            machineId: params.machineId,
            amount: params.amount,
            applicationFee: params.amount * 0.05,
            status: "Pending",
            qrCode: `00020101021226870014br.gov.bcb.pix2572pix.example.com/qr/v2/mock-${mockTxId}`,
            qrCodeBase64: "iVBORw0KGgoAAAANSUhEUgAAAJYAAACWAQAAAAAUekxPAAABUklEQVR4nNWWQWrDMBBFnxKD2pVyAwV6Dzvd9FQGp6SLHsu+iX0DZWeD4t+FnbSkBEpjQauV+QzzGP3RjI24PqfVNwn+uoZqgNxFb2tKwMblGY0UyCkB8FISRjChqp3a3ih6/PKMSXs3XeYftHlbKN8N7bS19fyZgRuPnzHi4S5GBsCwG+NTr2OcNZkvIZ3BLtNXUl8oAjZmFIIzZi3ufDNIkmyjg8hZS1KCvpKkHhcBp4QMoKQvlJJhR8DWSe+KQpE+7V1ZBbxGd0jCuGhVY1Y/ifuFNtdRA3akSulHPjFSei6Fkj5P6ocx3raYbr9QvqtzrgOUyo957j6O6oaXBfLdnrvTKi8kkbsEfhgpDM/K6LV53aoJCeqY9vnUtmlmyaQNO+iQyTBmk6gO2wS8rd3ea3RxBW5cliEFK6nFhKpVozjtc7sgo4bpXVz8MP/wH/gDo7EYHNztgj4AAAAASUVORK5CYII=",
            createdAt: new Date().toISOString()
        };
        mockTransactions.push(newTx);
        localStorage.setItem("mock_transactions", JSON.stringify(mockTransactions));
        
        // Mock Logs
        const mockLogs = [
            { id: `log-1`, transactionId: mockTxId, timestamp: new Date().toISOString(), logType: "Info", message: `Solicitada cobrança Pix de R$ ${params.amount} na máquina.` },
            { id: `log-2`, transactionId: mockTxId, timestamp: new Date(Date.now() + 500).toISOString(), logType: "Info", message: "QR Code Pix gerado em modo simulado." }
        ];
        localStorage.setItem(`mock_logs_${mockTxId}`, JSON.stringify(mockLogs));
        
        return {
            transactionId: mockTxId,
            qrCode: newTx.qrCode,
            qrCodeBase64: newTx.qrCodeBase64,
            status: "Pending",
            applicationFee: newTx.applicationFee,
            applicationFeeApplied: true,
            simulated: true
        };
    }
    const response = await authFetch(`${API_BASE_URL}/payments/pix-qr`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(params)
    });
    if (!response.ok) {
        const errorData = await response.json().catch(() => ({})) as {
            message?: string;
            transactionId?: string;
        };
        throw new PaymentRequestError(
            errorData.message || "Erro ao gerar QR Code Pix.",
            errorData.transactionId,
        );
    }
    return response.json();
}

export async function simulateWebhook(transactionId: string, approved: boolean, sendTelemetryToMachine = true): Promise<void> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        const mockTransactions = JSON.parse(localStorage.getItem("mock_transactions") || "[]");
        const idx = mockTransactions.findIndex(t => t.id == transactionId);
        if (idx > -1) {
            mockTransactions[idx].status = approved ? "Approved" : "Rejected";
            localStorage.setItem("mock_transactions", JSON.stringify(mockTransactions));
            
            // Append Simulated Telemetry Logs
            const logs = JSON.parse(localStorage.getItem(`mock_logs_${transactionId}`) || "[]");
            logs.push({ id: `log-3`, transactionId, timestamp: new Date().toISOString(), logType: "Info", message: `Webhook Simulado: Pagamento ${(approved ? "APROVADO" : "RECUSADO")}` });

            if (!sendTelemetryToMachine) {
                logs.push({ id: `log-4`, transactionId, timestamp: new Date().toISOString(), logType: "Info", message: "Simulador configurado para não enviar retorno MDB para a máquina." });
                localStorage.setItem(`mock_logs_${transactionId}`, JSON.stringify(logs));
                return;
            }
            
            // Simular sequencia de telemetria local
            setTimeout(() => {
                logs.push({ id: `log-4`, transactionId, timestamp: new Date().toISOString(), logType: "CommandSent", message: "Enviando comando: ABRIR_SESSAO" });
                localStorage.setItem(`mock_logs_${transactionId}`, JSON.stringify(logs));
            }, 1000);
            
            setTimeout(() => {
                logs.push({ id: `log-5`, transactionId, timestamp: new Date().toISOString(), logType: "CommandSent", message: approved ? "Enviando comando: VENDA_APROVADA" : "Enviando comando: VENDA_NEGADA" });
                localStorage.setItem(`mock_logs_${transactionId}`, JSON.stringify(logs));
            }, 3000);

            setTimeout(() => {
                logs.push({ id: `log-6`, transactionId, timestamp: new Date().toISOString(), logType: "CommandSent", message: "Enviando comando: FECHAR_SESSAO" });
                logs.push({ id: `log-7`, transactionId, timestamp: new Date().toISOString(), logType: "Info", message: "Sequência de telemetria MDB remota concluída." });
                localStorage.setItem(`mock_logs_${transactionId}`, JSON.stringify(logs));
            }, 5000);
        }
        return;
    }
    const response = await authFetch(`${API_BASE_URL}/payments/simulate-webhook`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ transactionId, approved, sendTelemetryToMachine })
    });
    if (!response.ok) throw new Error("Erro ao simular webhook de pagamento.");
}

export async function getTransactions(machineId?: string, transactionId?: string): Promise<PaymentTransaction[]> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        const list = JSON.parse(localStorage.getItem("mock_transactions") || "[]") as Array<PaymentTransaction & { qrCode?: string; qrCodeBase64?: string }>;
        return list
            .filter((transaction) => !machineId || transaction.machineId === machineId)
            .filter((transaction) => !transactionId || transaction.id === transactionId)
            .map((transaction) => {
                const summary = { ...transaction };
                Reflect.deleteProperty(summary, "qrCode");
                Reflect.deleteProperty(summary, "qrCodeBase64");
                return summary;
            });
    }
    const params = new URLSearchParams();
    if (machineId) params.set("machineId", machineId);
    if (transactionId) params.set("transactionId", transactionId);
    const serializedParams = params.toString();
    const query = serializedParams ? `?${serializedParams}` : "";
    const response = await authFetch(`${API_BASE_URL}/payments/transactions${query}`);
    if (!response.ok) throw new Error("Erro ao buscar transações.");
    return response.json();
}

export async function getPaymentTransactionHistory(
    page = 1,
    pageSize = 25,
    machineId?: string,
    transactionId?: string,
    status?: string,
): Promise<PaymentTransactionHistoryPage> {
    const safePage = Math.max(1, Math.floor(page));
    const safePageSize = Math.min(100, Math.max(1, Math.floor(pageSize)));
    if (import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true") {
        const filtered = (await getTransactions(machineId, transactionId))
            .filter(transaction => !status || transaction.status === status);
        const total = filtered.length;
        const totalPages = Math.max(1, Math.ceil(total / safePageSize));
        const currentPage = Math.min(safePage, totalPages);
        const startIndex = (currentPage - 1) * safePageSize;
        return {
            items: filtered.slice(startIndex, startIndex + safePageSize),
            total,
            page: currentPage,
            pageSize: safePageSize,
            totalPages,
        };
    }

    const params = new URLSearchParams({ page: String(safePage), pageSize: String(safePageSize) });
    if (machineId) params.set("machineId", machineId);
    if (transactionId) params.set("transactionId", transactionId);
    if (status) params.set("status", status);
    const response = await authFetch(`${API_BASE_URL}/payments/transactions/history?${params.toString()}`);
    if (!response.ok) throw new Error(await readApiError(response, "Erro ao buscar o histórico de pagamentos."));
    return response.json();
}

export async function getTransactionLogs(
    transactionId: string,
    page = 1,
    pageSize = 50,
): Promise<TransactionTelemetryLogPage> {
    const safePage = Math.max(1, Math.floor(page));
    const safePageSize = Math.min(100, Math.max(1, Math.floor(pageSize)));
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        const allItems = JSON.parse(localStorage.getItem(`mock_logs_${transactionId}`) || "[]") as TransactionTelemetryLog[];
        const totalPages = Math.max(1, Math.ceil(allItems.length / safePageSize));
        const currentPage = Math.min(safePage, totalPages);
        return {
            items: allItems.slice((currentPage - 1) * safePageSize, currentPage * safePageSize),
            total: allItems.length,
            page: currentPage,
            pageSize: safePageSize,
            totalPages,
        };
    }
    const params = new URLSearchParams({ page: String(safePage), pageSize: String(safePageSize) });
    const response = await authFetch(`${API_BASE_URL}/payments/transactions/${transactionId}/logs?${params.toString()}`);
    if (!response.ok) throw new Error("Erro ao buscar logs da transação.");
    const items = await response.json() as TransactionTelemetryLog[];
    const readHeader = (name: string, fallback: number) => {
        const value = Number(response.headers.get(name));
        return Number.isSafeInteger(value) && value >= 0 ? value : fallback;
    };
    return {
        items,
        total: readHeader("X-Total-Count", items.length),
        page: readHeader("X-Page", safePage),
        pageSize: readHeader("X-Page-Size", safePageSize),
        totalPages: readHeader("X-Total-Pages", 1),
    };
}

export interface AdminLog {
    id: string;
    transactionId: string;
    timestamp: string;
    logType: string;
    message: string;
    transactionAmount: number;
    machineName: string;
    companyName: string;
    rawResponse?: string | null;
}

export interface PaginatedAdminLogs {
    items: AdminLog[];
    page: number;
    pageSize: number;
    totalItems: number;
    totalPages: number;
}

export interface DeviceMessageInboxFailure {
    id: string;
    machineId: string;
    serial: string;
    messageId: number;
    receivedAt: string;
    attempts: number;
    lastError?: string | null;
}

export interface DeviceMessageInboxSummary {
    counts: Record<string, number>;
    failures: DeviceMessageInboxFailure[];
    page: number;
    pageSize: number;
    totalFailed: number;
    totalPages: number;
}

export interface ReconciliationQueueItem {
    id: string;
    companyId: string;
    companyName: string;
    machineId: string;
    machineName: string;
    amount: number;
    status: "Pending" | "ReconciliationRequired";
    reconciliationReason?: string | null;
    mercadoPagoStatus?: string | null;
    mercadoPagoStatusDetail?: string | null;
    mercadoPagoPaymentId?: string | null;
    createdAt: string;
    lastReconciliationAt?: string | null;
}

export interface PaymentReconciliationResolutionRequest {
    resolution: "approved" | "rejected" | "cancelled" | "refunded" | "chargeback_settled" | "chargeback_reimbursed";
    evidenceReference: string;
    expectedProviderStatus: string;
    expectedProviderStatusDetail: string | null;
    expectedReconciliationReason: string | null;
    expectedLastReconciliationAt: string | null;
    confirmedRefundedAmount?: number;
}

export interface ReconciliationQueueSummary {
    counts: { pending: number; reconciliationRequired: number };
    items: ReconciliationQueueItem[];
    page: number;
    pageSize: number;
    totalItems: number;
    totalPages: number;
}

export type PaymentWebhookStatus = "Pending" | "Retry" | "Processing" | "Processed" | "Failed";

export interface PaymentWebhookInboxItem {
    id: string;
    paymentId: string;
    eventType: string;
    eventAction: string;
    format: string;
    authenticationMode: string;
    receivedAt: string;
    status: PaymentWebhookStatus;
    attempts: number;
    nextAttemptAt?: string | null;
    processedAt?: string | null;
    lastError?: string | null;
}

export interface PaymentWebhookInboxSummary {
    items: PaymentWebhookInboxItem[];
    page: number;
    pageSize: number;
    totalItems: number;
    totalPages: number;
}

export interface BackgroundWorkerHealth {
    name: string;
    status: "Healthy" | "Degraded" | "Stale" | "Stopped" | "NotStarted" | string;
    startedAt?: string | null;
    lastHeartbeatAt?: string | null;
    lastSuccessAt?: string | null;
    consecutiveFailures: number;
    lastErrorType?: string | null;
}

export interface WorkerHealthQueueMetrics {
    available: boolean;
    machineConnections?: {
        monitored: number;
        online: number;
        staleOnline: number;
        onlineWithoutOwner: number;
        onlineWithoutCommandRouting?: number;
        legacyRelay?: {
            eligibleConnections: number;
            configured: boolean;
            healthy: boolean | null;
        };
        readinessGate?: {
            onlineInGateScope: number;
            unroutable: number;
            withoutOwner: number;
            withoutCommandRouting: number;
            companyMismatch: number;
        };
    };
    deviceMessageInbox?: Record<string, number>;
    oldestDeviceMessageAgeSeconds?: number | null;
    telemetryCommandDispatches?: Record<string, number>;
    oldestPendingTelemetryDispatchAgeSeconds?: number | null;
    oldestProcessingTelemetryDispatchAgeSeconds?: number | null;
    localTelemetryCommands?: {
        pendingIntents: number;
        oldestPendingIntentAgeSeconds: number | null;
        unresolvedResults: number;
        oldestUnresolvedResultAgeSeconds: number | null;
    };
    webhookInbox?: Record<string, number>;
    oldestWebhookAgeSeconds?: number | null;
    financialOutbox?: Record<string, number>;
    oldestPendingFinancialOutboxAgeSeconds?: number | null;
    oldestDeferredLegacyFinancialOutboxAgeSeconds?: number | null;
    earliestProcessingFinancialOutboxLeaseExpiryAt?: string | null;
    pendingPayments?: number;
    oldestPendingPaymentAgeSeconds?: number | null;
}

export interface WorkerHealthSummary {
    generatedAt: string;
    sourceCommit: string | null;
    workers: BackgroundWorkerHealth[];
    hasUnhealthyWorkers: boolean;
    queueMetrics: WorkerHealthQueueMetrics;
}

export interface LegacyMachineConnection {
    machineId: string;
    machineName: string;
    serialNumber: string;
    companyName: string;
    lastSeenAt: string;
    activeSessionState: string | null;
    activeSessionStartedAt: string | null;
}

export interface LegacyMachineConnectionsSummary {
    generatedAt: string;
    relay: { configured: boolean; healthy: boolean | null };
    items: LegacyMachineConnection[];
    page: number;
    pageSize: number;
    totalItems: number;
    totalPages: number;
}

export async function getLegacyMachineConnections(page: number = 1, pageSize: number = 50): Promise<LegacyMachineConnectionsSummary> {
    if (import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true") {
        return { generatedAt: new Date().toISOString(), relay: { configured: false, healthy: null }, items: [], page, pageSize, totalItems: 0, totalPages: 0 };
    }
    const response = await authFetch(`${API_BASE_URL}/admin/legacy-machine-connections?page=${page}&pageSize=${pageSize}`);
    if (!response.ok) throw new Error("Não foi possível consultar as conexões da API anterior.");
    return response.json();
}

export async function getWorkerHealth(): Promise<WorkerHealthSummary> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        return {
            generatedAt: new Date().toISOString(),
            sourceCommit: null,
            workers: [
                { name: "telemetry-inbox", status: "Healthy", startedAt: new Date().toISOString(), lastHeartbeatAt: new Date().toISOString(), lastSuccessAt: new Date().toISOString(), consecutiveFailures: 0 },
                { name: "telemetry-command-dispatch", status: "Healthy", startedAt: new Date().toISOString(), lastHeartbeatAt: new Date().toISOString(), lastSuccessAt: new Date().toISOString(), consecutiveFailures: 0 },
                { name: "mercado-pago-webhook", status: "Healthy", startedAt: new Date().toISOString(), lastHeartbeatAt: new Date().toISOString(), lastSuccessAt: new Date().toISOString(), consecutiveFailures: 0 },
            ],
            hasUnhealthyWorkers: false,
            queueMetrics: {
                available: true,
                machineConnections: {
                    monitored: 3,
                    online: 2,
                    staleOnline: 0,
                    onlineWithoutOwner: 0,
                    onlineWithoutCommandRouting: 0,
                    readinessGate: { onlineInGateScope: 2, unroutable: 0, withoutOwner: 0, withoutCommandRouting: 0, companyMismatch: 0 },
                },
                deviceMessageInbox: { Pending: 0, Retry: 0, Processing: 0, Failed: 0, Processed: 0 },
                oldestDeviceMessageAgeSeconds: null,
                telemetryCommandDispatches: { Pending: 0, Processing: 0, Failed: 0 },
                oldestPendingTelemetryDispatchAgeSeconds: null,
                oldestProcessingTelemetryDispatchAgeSeconds: null,
                localTelemetryCommands: {
                    pendingIntents: 0,
                    oldestPendingIntentAgeSeconds: null,
                    unresolvedResults: 0,
                    oldestUnresolvedResultAgeSeconds: null,
                },
                webhookInbox: { Pending: 0, Retry: 0, Processing: 0, Failed: 0, Processed: 0 },
                oldestWebhookAgeSeconds: null,
                financialOutbox: { pending: 0, processing: 0, failed: 0, processed: 0, deferred_legacy_overlap: 0 },
                oldestPendingFinancialOutboxAgeSeconds: null,
                oldestDeferredLegacyFinancialOutboxAgeSeconds: null,
                earliestProcessingFinancialOutboxLeaseExpiryAt: null,
                pendingPayments: 0,
                oldestPendingPaymentAgeSeconds: null,
            },
        };
    }

    const response = await authFetch(`${API_BASE_URL}/admin/worker-health`);
    if (!response.ok) throw new Error("Não foi possível consultar a saúde operacional.");
    return response.json();
}

export async function getReconciliationQueue(page: number = 1, pageSize: number = 20): Promise<ReconciliationQueueSummary> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        return { counts: { pending: 0, reconciliationRequired: 0 }, items: [], page, pageSize, totalItems: 0, totalPages: 0 };
    }
    const response = await authFetch(`${API_BASE_URL}/payments/admin/reconciliation-queue?page=${page}&pageSize=${pageSize}`);
    if (!response.ok) throw new Error("Não foi possível consultar a fila de conciliação financeira.");
    return response.json();
}

export async function resolvePaymentReconciliation(
    id: string,
    request: PaymentReconciliationResolutionRequest
): Promise<void> {
    const response = await authFetch(`${API_BASE_URL}/payments/admin/reconciliation-queue/${encodeURIComponent(id)}/resolve`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(request)
    });
    if (!response.ok) {
        const error = await response.json().catch(() => ({}));
        throw new Error(error.message ?? "Não foi possível registrar a conciliação financeira.");
    }
}

export async function getPaymentWebhookInbox(
    status: PaymentWebhookStatus | "All" = "Failed",
    page: number = 1,
    pageSize: number = 20
): Promise<PaymentWebhookInboxSummary> {
    const params = new URLSearchParams({ page: String(page), pageSize: String(pageSize) });
    if (status !== "All") params.set("status", status);
    const response = await authFetch(`${API_BASE_URL}/payments/admin/webhook-inbox?${params.toString()}`);
    if (!response.ok) throw new Error("Não foi possível consultar a fila de webhooks do Mercado Pago.");
    return response.json();
}

export async function retryPaymentWebhook(id: string): Promise<void> {
    const response = await authFetch(`${API_BASE_URL}/payments/admin/webhook-inbox/${encodeURIComponent(id)}/retry`, {
        method: "POST"
    });
    if (!response.ok) {
        const error = await response.json().catch(() => ({}));
        throw new Error(error.message ?? "Não foi possível reenfileirar o webhook.");
    }
}

export async function getDeviceMessageInbox(page: number = 1, pageSize: number = 20): Promise<DeviceMessageInboxSummary> {
    const response = await authFetch(`${API_BASE_URL}/admin/device-message-inbox?page=${page}&pageSize=${pageSize}`);
    if (!response.ok) throw new Error("Não foi possível consultar a fila durável de telemetria.");
    return response.json();
}

export async function retryDeviceMessageInbox(id: string): Promise<void> {
    const response = await authFetch(`${API_BASE_URL}/admin/device-message-inbox/${encodeURIComponent(id)}/retry`, {
        method: "POST"
    });
    if (!response.ok) {
        const error = await response.json().catch(() => ({}));
        throw new Error(error.error === "message_not_failed"
            ? "Esta mensagem não está mais em falha. Atualize a lista."
            : "Não foi possível reenfileirar a mensagem.");
    }
}

export async function getAdminLogs(page: number = 1, pageSize: number = 20, search: string = ""): Promise<PaginatedAdminLogs> {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
        const mockItems: AdminLog[] = [
            {
                id: "1",
                transactionId: "trans-abc",
                timestamp: new Date().toISOString(),
                logType: "Error",
                message: "Erro retornado pelo Mercado Pago: Erro da API Mercado Pago (BadRequest): You cannot use application_fee with this payment.",
                transactionAmount: 15.0,
                machineName: "Vending Machine Portaria",
                companyName: "ACME Machines LTDA",
                rawResponse: '{"error": "bad_request", "message": "You cannot use application_fee with this payment", "status": 400, "cause": [{"code": "2030", "description": "You cannot use application_fee with this payment"}]}'
            },
            {
                id: "2",
                transactionId: "trans-abc",
                timestamp: new Date(Date.now() - 5000).toISOString(),
                logType: "Info",
                message: "Solicitada cobrança Pix de R$ 15,00 na máquina Vending Machine Portaria (Taxa do site: R$ 0,75).",
                transactionAmount: 15.0,
                machineName: "Vending Machine Portaria",
                companyName: "ACME Machines LTDA"
            },
            {
                id: "3",
                transactionId: "trans-xyz",
                timestamp: new Date(Date.now() - 3600000).toISOString(),
                logType: "Info",
                message: "QR Code Pix gerado com sucesso pelo Mercado Pago. Payment ID: 9988776655",
                transactionAmount: 8.5,
                machineName: "Vending Central",
                companyName: "Vending Express",
                rawResponse: '{"id": 9988776655, "status": "pending", "status_detail": "pending_waiting_transfer"}'
            }
        ];

        const filtered = mockItems.filter(item => 
            !search || 
            item.message.toLowerCase().includes(search.toLowerCase()) || 
            item.logType.toLowerCase().includes(search.toLowerCase()) ||
            item.companyName.toLowerCase().includes(search.toLowerCase()) ||
            item.machineName.toLowerCase().includes(search.toLowerCase())
        );

        const totalItems = filtered.length;
        const totalPages = Math.ceil(totalItems / pageSize);
        const paginated = filtered.slice((page - 1) * pageSize, page * pageSize);

        return {
            items: paginated,
            page,
            pageSize,
            totalItems,
            totalPages
        };
    }

    const searchParam = search ? `&search=${encodeURIComponent(search)}` : "";
    const response = await authFetch(`${API_BASE_URL}/payments/admin/logs?page=${page}&pageSize=${pageSize}${searchParam}`);
    if (!response.ok) throw new Error("Erro ao carregar logs administrativos.");
    return response.json();
}
