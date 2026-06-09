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

export const API_BASE_URL = import.meta.env.VITE_API_URL || "http://localhost:5118/api";

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
            email: i === 0 ? "dev.ivan@gmail.com" : i === 1 ? "joao@vmmanager.com" : `operador${i + 1}@vmmanager.com`,
            role: i === 0 ? "Admin" : "Operator",
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
        createdBy: string;
        partners: string[];
        createdAt: string;
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
                createdBy: "Admin Ivan",
                partners: ["João da Silva", "Maria Santos"],
                createdAt: new Date(Date.now() - 30 * 24 * 60 * 60 * 1000).toISOString()
            },
            {
                id: "55555555-5555-5555-5555-555555555555",
                name: "Sabor & Cia Vending",
                createdBy: "Eduardo Souza",
                partners: ["Ana Julia", "Pedro Mendes"],
                createdAt: new Date(Date.now() - 15 * 24 * 60 * 60 * 1000).toISOString()
            },
            {
                id: "99999999-9999-9999-9999-999999999999",
                name: "Express Café Vending",
                createdBy: "Roberto Lima",
                partners: [],
                createdAt: new Date(Date.now() - 5 * 24 * 60 * 60 * 1000).toISOString()
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
