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

export async function getMachines(): Promise<Machine[]> {
    if (isTestEnv()) {
        return mockMachines;
    }
    const response = await fetch(`${API_BASE_URL}/machines`);
    if (!response.ok) throw new Error("Erro ao buscar máquinas da API");
    return response.json();
}

export async function getMachine(id: string): Promise<Machine | null> {
    if (isTestEnv()) {
        return mockMachines.find(m => m.id === id) || null;
    }
    const response = await fetch(`${API_BASE_URL}/machines/${id}`);
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
    const response = await fetch(`${API_BASE_URL}/machines`, {
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
    const response = await fetch(`${API_BASE_URL}/machines/${id}`, {
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
    const response = await fetch(`${API_BASE_URL}/clients`);
    if (!response.ok) throw new Error("Erro ao buscar clientes da API");
    return response.json();
}

export async function getProducts(): Promise<FullProduct[]> {
    if (isTestEnv()) {
        return mockProducts;
    }
    const response = await fetch(`${API_BASE_URL}/products`);
    if (!response.ok) throw new Error("Erro ao buscar produtos da API");
    return response.json();
}

export async function getProductTypes(): Promise<ProductType[]> {
    if (isTestEnv()) {
        return mockProductTypes;
    }
    const response = await fetch(`${API_BASE_URL}/products/types`);
    if (!response.ok) throw new Error("Erro ao buscar tipos de produtos da API");
    return response.json();
}

export async function getTopProducts(): Promise<Product[]> {
    if (isTestEnv()) {
        return mockTopProducts;
    }
    const response = await fetch(`${API_BASE_URL}/products/top`);
    if (!response.ok) throw new Error("Erro ao buscar mais vendidos da API");
    return response.json();
}

export async function getBottomProducts(): Promise<Product[]> {
    if (isTestEnv()) {
        return mockBottomProducts;
    }
    const response = await fetch(`${API_BASE_URL}/products/bottom`);
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
    const response = await fetch(`${API_BASE_URL}/dashboard/stats`);
    if (!response.ok) throw new Error("Erro ao buscar estatísticas do painel da API");
    return response.json();
}
