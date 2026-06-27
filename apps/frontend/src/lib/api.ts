import {
    machines as mockMachines,
    clients as mockClients,
    products as mockProducts,
    productTypes as mockProductTypes,
    topProducts as mockTopProducts,
    bottomProducts as mockBottomProducts,
    type Machine,
    type Client,
    type FullProduct,
    type Product,
    type ProductType
} from "@/data/mockData";

const rawApiUrl = import.meta.env.VITE_API_URL || "http://localhost:5118/api";
export const API_BASE_URL = rawApiUrl.endsWith("/") ? rawApiUrl.slice(0, -1) : rawApiUrl;

const isTestEnv = () => import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true";

async function authFetch(url: string, options: RequestInit = {}): Promise<Response> {
    const token = sessionStorage.getItem("token") || localStorage.getItem("token");
    const headers = new Headers(options.headers || {});
    
    if (token) {
        headers.set("Authorization", `Bearer ${token}`);
    }
    
    return fetch(url, { ...options, headers });
}

export async function getMachines(): Promise<Machine[]> {
    if (isTestEnv()) {
        return mockMachines;
    }
    const response = await authFetch(`${API_BASE_URL}/machines`);
    if (!response.ok) throw new Error("Erro ao buscar máquinas da API");
    return response.json();
}

export async function getMachine(id: string): Promise<Machine | null> {
    if (isTestEnv()) {
        return mockMachines.find(m => m.id === id) || null;
    }
    const response = await authFetch(`${API_BASE_URL}/machines/${id}`);
    if (!response.ok) return null;
    return response.json();
}

export async function createMachine(machine: Partial<Machine>): Promise<Machine> {
    if (isTestEnv()) {
        const newId = `VM-${String(mockMachines.length + 1).padStart(3, '0')}`;
        const newMachine: Machine = {
            id: newId,
            name: machine.name || "",
            clientName: machine.clientName || "Hospital São Luiz",
            location: machine.location || "Lobby Central",
            status: machine.status || "online",
            stockLevel: machine.stockLevel ?? 100,
            revenue30d: machine.revenue30d ?? 0,
            totalSales30d: machine.totalSales30d ?? 0,
            lastSync: "Agora",
            serialNumber: machine.serialNumber || ""
        };
        mockMachines.push(newMachine);
        return newMachine;
    }
    const response = await authFetch(`${API_BASE_URL}/machines`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(machine)
    });
    if (!response.ok) throw new Error("Erro ao criar máquina na API");
    return response.json();
}

export async function updateMachine(id: string, machine: Partial<Machine>): Promise<void> {
    if (isTestEnv()) {
        const idx = mockMachines.findIndex(m => m.id === id);
        if (idx > -1) {
            mockMachines[idx] = { ...mockMachines[idx], ...machine } as Machine;
        }
        return;
    }
    const response = await authFetch(`${API_BASE_URL}/machines/${id}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(machine)
    });
    if (!response.ok) throw new Error("Erro ao atualizar máquina na API");
}

export async function getClients(): Promise<Client[]> {
    if (isTestEnv()) {
        return mockClients;
    }
    const response = await authFetch(`${API_BASE_URL}/clients`);
    if (!response.ok) throw new Error("Erro ao buscar clientes da API");
    return response.json();
}

export async function getProducts(): Promise<FullProduct[]> {
    if (isTestEnv()) {
        return mockProducts;
    }
    const response = await authFetch(`${API_BASE_URL}/products`);
    if (!response.ok) throw new Error("Erro ao buscar produtos da API");
    return response.json();
}

export async function getProductTypes(): Promise<ProductType[]> {
    if (isTestEnv()) {
        return mockProductTypes;
    }
    const response = await authFetch(`${API_BASE_URL}/products/types`);
    if (!response.ok) throw new Error("Erro ao buscar tipos de produtos da API");
    return response.json();
}

export async function getTopProducts(): Promise<Product[]> {
    if (isTestEnv()) {
        return mockTopProducts;
    }
    const response = await authFetch(`${API_BASE_URL}/products/top`);
    if (!response.ok) throw new Error("Erro ao buscar mais vendidos da API");
    return response.json();
}

export async function getBottomProducts(): Promise<Product[]> {
    if (isTestEnv()) {
        return mockBottomProducts;
    }
    const response = await authFetch(`${API_BASE_URL}/products/bottom`);
    if (!response.ok) throw new Error("Erro ao buscar menos vendidos da API");
    return response.json();
}

export async function getDashboardStats(): Promise<any> {
    if (isTestEnv()) {
        const totalRevenue = mockMachines.reduce((s, m) => s + m.revenue30d, 0);
        const totalSales = mockMachines.reduce((s, m) => s + m.totalSales30d, 0);
        const online = mockMachines.filter((m) => m.status === "online").length;
        const warning = mockMachines.filter((m) => m.status === "warning").length;
        const offline = mockMachines.filter((m) => m.status === "offline").length;

        return {
            totalRevenue30d: totalRevenue,
            totalSales30d: totalSales,
            onlineMachines: online,
            warningMachines: warning,
            offlineMachines: offline,
            revenueHistory: [
                { day: "Seg", revenue: 2840 },
                { day: "Ter", revenue: 3120 },
                { day: "Qua", revenue: 2960 },
                { day: "Qui", revenue: 3450 },
                { day: "Sex", revenue: 3890 },
                { day: "Sáb", revenue: 4210 },
                { day: "Dom", revenue: 2180 }
            ]
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

export async function getUsers(pageNumber = 1, pageSize = 10, searchTerm = ""): Promise<PaginatedUsers> {
    if (isTestEnv()) {
        const allMockUsers = Array.from({ length: 45 }, (_, i) => ({
            id: `usr-${i + 1}`,
            name: i === 0 ? "Admin Ivan" : i === 1 ? "João da Silva" : `Usuário Operador ${i + 1}`,
            email: i === 0 ? "dev.ivan@gmail.com" : i === 1 ? "joao@vendmachine.com.br" : `operador${i + 1}@vendmachine.com.br`,
            role: i === 0 ? "Admin" : "Operator",
            cpf: i === 0 ? "123.456.789-00" : i === 1 ? "987.654.321-99" : `000.000.000-${String(i).padStart(2, '0')}`,
            companyId: i % 2 === 0 ? "11111111-1111-1111-1111-111111111111" : "55555555-5555-5555-5555-555555555555",
            companyName: i % 2 === 0 ? "ACME Machines LTDA" : "Sabor & Cia Vending",
            createdAt: new Date(Date.now() - i * 24 * 60 * 60 * 1000).toISOString()
        }));

        let filtered = allMockUsers;
        if (searchTerm) {
            const search = searchTerm.toLowerCase();
            filtered = allMockUsers.filter(u => u.name.toLowerCase().includes(search) || u.email.toLowerCase().includes(search));
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

export interface PaginatedCompanies {
    items: {
        id: string;
        name: string;
        cnpj?: string;
        createdBy: string;
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
    if (isTestEnv()) {
        const allMockCompanies = [
            {
                id: "11111111-1111-1111-1111-111111111111",
                name: "ACME Machines LTDA",
                cnpj: "12.345.678/0001-99",
                createdBy: "Admin Ivan",
                partners: ["João da Silva", "Maria Santos"],
                createdAt: new Date(Date.now() - 30 * 24 * 60 * 60 * 1000).toISOString(),
                enabledIntegrations: ["Mercado Pago"]
            },
            {
                id: "55555555-5555-5555-5555-555555555555",
                name: "Sabor & Cia Vending",
                cnpj: "98.765.432/0001-00",
                createdBy: "Eduardo Souza",
                partners: ["Ana Julia", "Pedro Mendes"],
                createdAt: new Date(Date.now() - 15 * 24 * 60 * 60 * 1000).toISOString(),
                enabledIntegrations: []
            },
            {
                id: "99999999-9999-9999-9999-999999999999",
                name: "Express Café Vending",
                cnpj: "11.222.333/0001-44",
                createdBy: "Roberto Lima",
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

export interface SystemSettings {
    id?: string;
    applicationFeePercent: number;
    updatedAt?: string;
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
    publicKey: string;
    clientId?: string;
    clientSecret?: string;
    isActive: boolean;
    createdAt?: string;
    updatedAt?: string;
}

export interface PaymentTransaction {
    id: string;
    machineId: string;
    machine?: Machine;
    amount: number;
    applicationFee: number;
    status: "Pending" | "Approved" | "Rejected" | "Failed" | "Refunded";
    mercadoPagoPaymentId?: string;
    mercadoPagoStatus?: string;
    mercadoPagoStatusDetail?: string;
    qrCode: string;
    qrCodeBase64: string;
    createdAt: string;
    completedAt?: string;
}

export interface TransactionTelemetryLog {
    id: string;
    transactionId: string;
    timestamp: string;
    logType: "Info" | "CommandSent" | "AckReceived" | "Error" | "RefundTriggered";
    message: string;
}

export async function getGlobalSettings(): Promise<SystemSettings> {
    if (isTestEnv()) {
        const local = localStorage.getItem("mock_global_settings");
        return local ? JSON.parse(local) : { applicationFeePercent: 5.0 };
    }
    const response = await authFetch(`${API_BASE_URL}/payments/global-settings`);
    if (!response.ok) throw new Error("Erro ao carregar configurações de pagamento globais.");
    return response.json();
}

export async function updateGlobalSettings(settings: SystemSettings): Promise<void> {
    if (isTestEnv()) {
        localStorage.setItem("mock_global_settings", JSON.stringify(settings));
        return;
    }
    const response = await authFetch(`${API_BASE_URL}/payments/global-settings`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(settings)
    });
    if (!response.ok) throw new Error("Erro ao salvar taxa global.");
}

export async function getIntegration(): Promise<MercadoPagoIntegration> {
    if (isTestEnv()) {
        const local = localStorage.getItem("mock_mp_integration");
        const userStr = sessionStorage.getItem("user");
        const companyId = userStr ? JSON.parse(userStr).companyId : "11111111-1111-1111-1111-111111111111";
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

export async function saveIntegration(integration: MercadoPagoIntegration): Promise<void> {
    if (isTestEnv()) {
        localStorage.setItem("mock_mp_integration", JSON.stringify(integration));
        return;
    }
    const response = await authFetch(`${API_BASE_URL}/payments/integration`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(integration)
    });
    if (!response.ok) throw new Error("Erro ao salvar integração.");
}

export async function toggleIntegration(isActive: boolean): Promise<void> {
    if (isTestEnv()) {
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
}

export async function getOauthConfig(): Promise<OauthConfig> {
    if (isTestEnv()) {
        return {
            clientId: "7902965695113613",
            redirectUri: window.location.origin + "/"
        };
    }
    const response = await authFetch(`${API_BASE_URL}/payments/oauth/config`);
    if (!response.ok) throw new Error("Erro ao carregar configurações do OAuth.");
    return response.json();
}

export async function exchangeOauthCode(code: string, redirectUri: string): Promise<MercadoPagoIntegration> {
    if (isTestEnv()) {
        const userStr = sessionStorage.getItem("user");
        const companyId = userStr ? JSON.parse(userStr).companyId : "11111111-1111-1111-1111-111111111111";
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
        body: JSON.stringify({ code, redirectUri })
    });
    if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.message || "Erro ao realizar o callback do Mercado Pago.");
    }
    return response.json();
}

export async function disconnectIntegration(): Promise<void> {
    if (isTestEnv()) {
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
    if (isTestEnv()) {
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
            publicKey: "APP_USR-9876••••••••XYZ",
            clientId: "acme_client_id",
            clientSecret: "acme_••••••••secret",
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
    if (isTestEnv()) {
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
        const errorData = await response.json().catch(() => ({}));
        const err = new Error(errorData.message || "Erro ao gerar QR Code Pix.") as any;
        err.transactionId = errorData.transactionId;
        throw err;
    }
    return response.json();
}

export async function simulateWebhook(transactionId: string, approved: boolean): Promise<void> {
    if (isTestEnv()) {
        const mockTransactions = JSON.parse(localStorage.getItem("mock_transactions") || "[]");
        const idx = mockTransactions.findIndex(t => t.id == transactionId);
        if (idx > -1) {
            mockTransactions[idx].status = approved ? "Approved" : "Rejected";
            localStorage.setItem("mock_transactions", JSON.stringify(mockTransactions));
            
            // Append Simulated Telemetry Logs
            const logs = JSON.parse(localStorage.getItem(`mock_logs_${transactionId}`) || "[]");
            logs.push({ id: `log-3`, transactionId, timestamp: new Date().toISOString(), logType: "Info", message: `Webhook Simulado: Pagamento ${(approved ? "APROVADO" : "RECUSADO")}` });
            
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
        body: JSON.stringify({ transactionId, approved })
    });
    if (!response.ok) throw new Error("Erro ao simular webhook de pagamento.");
}

export async function getTransactions(machineId?: string): Promise<PaymentTransaction[]> {
    if (isTestEnv()) {
        const list = JSON.parse(localStorage.getItem("mock_transactions") || "[]");
        return list.filter((t: any) => !machineId || t.machineId === machineId);
    }
    const url = machineId ? `${API_BASE_URL}/payments/transactions?machineId=${machineId}` : `${API_BASE_URL}/payments/transactions`;
    const response = await authFetch(url);
    if (!response.ok) throw new Error("Erro ao buscar transações.");
    return response.json();
}

export async function getTransactionLogs(transactionId: string): Promise<TransactionTelemetryLog[]> {
    if (isTestEnv()) {
        return JSON.parse(localStorage.getItem(`mock_logs_${transactionId}`) || "[]");
    }
    const response = await authFetch(`${API_BASE_URL}/payments/transactions/${transactionId}/logs`);
    if (!response.ok) throw new Error("Erro ao buscar logs da transação.");
    return response.json();
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

export async function getAdminLogs(page: number = 1, pageSize: number = 20, search: string = ""): Promise<PaginatedAdminLogs> {
    if (isTestEnv()) {
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
