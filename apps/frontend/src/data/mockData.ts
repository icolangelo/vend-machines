export interface Machine {
  id: string;
  name: string;
  clientName: string;
  location: string;
  status: "online" | "offline" | "warning";
  stockLevel: number; // 0-100
  revenue30d: number;
  totalSales30d: number;
  lastSync: string;
  serialNumber: string;
}

export interface Client {
  name: string;
  machineCount: number;
  revenue30d: number;
  totalSales30d: number;
}

export interface Product {
    name: string;
    totalSold: number;
    revenue: number;
    trend: number; // % change
}

export interface FullProduct {
    id: string;
    code: string;
    name: string;
    description: string;
    typeId: string;
    isAlcoholic: boolean;
    cost: number;
}

export interface ProductType {
  id: string;
  name: string;
}

export let productTypes: ProductType[] = [
  { id: "pt-1", name: "Bebidas Frias" },
  { id: "pt-2", name: "Bebidas Quentes" },
  { id: "pt-3", name: "Carne" },
  { id: "pt-4", name: "Doces" },
  { id: "pt-5", name: "Outros" },
  { id: "pt-6", name: "Refeição" },
  { id: "pt-7", name: "Sanduíche" },
  { id: "pt-8", name: "Snacks" },
  { id: "pt-9", name: "Yogurt" },
];

export const machines: Machine[] = [
  { id: "VM-001", name: "Lobby A1", clientName: "Hospital São Luiz", location: "Lobby Principal", status: "online", stockLevel: 82, revenue30d: 4230, totalSales30d: 847, lastSync: "2 min atrás", serialNumber: "SN-123456" },
  { id: "VM-002", name: "Refeitório B2", clientName: "Hospital São Luiz", location: "Refeitório 2º andar", status: "warning", stockLevel: 18, revenue30d: 3890, totalSales30d: 778, lastSync: "5 min atrás", serialNumber: "SN-987654" },
  { id: "VM-003", name: "UTI C1", clientName: "Hospital São Luiz", location: "Corredor UTI", status: "online", stockLevel: 65, revenue30d: 2150, totalSales30d: 430, lastSync: "1 min atrás", serialNumber: "SN-555555" },
  { id: "VM-004", name: "Recepção D1", clientName: "Faculdade Anhanguera", location: "Bloco D", status: "online", stockLevel: 91, revenue30d: 5670, totalSales30d: 1134, lastSync: "3 min atrás", serialNumber: "SN-004" },
  { id: "VM-005", name: "Cantina E1", clientName: "Faculdade Anhanguera", location: "Cantina Central", status: "offline", stockLevel: 0, revenue30d: 0, totalSales30d: 0, lastSync: "2 dias atrás", serialNumber: "SN-005" },
  { id: "VM-006", name: "Hall F1", clientName: "Condomínio Alphaville", location: "Hall de Entrada", status: "online", stockLevel: 45, revenue30d: 1890, totalSales30d: 378, lastSync: "4 min atrás", serialNumber: "SN-006" },
  { id: "VM-007", name: "Academia G1", clientName: "Condomínio Alphaville", location: "Academia", status: "warning", stockLevel: 12, revenue30d: 3420, totalSales30d: 684, lastSync: "8 min atrás", serialNumber: "SN-007" },
  { id: "VM-008", name: "Terminal H1", clientName: "Rodoviária Tietê", location: "Terminal 3", status: "online", stockLevel: 73, revenue30d: 8940, totalSales30d: 1788, lastSync: "1 min atrás", serialNumber: "SN-008" },
  { id: "VM-009", name: "Embarque I1", clientName: "Rodoviária Tietê", location: "Sala de Embarque", status: "online", stockLevel: 56, revenue30d: 7230, totalSales30d: 1446, lastSync: "2 min atrás", serialNumber: "SN-009" },
  { id: "VM-010", name: "Plataforma J1", clientName: "Rodoviária Tietê", location: "Plataforma 12", status: "warning", stockLevel: 22, revenue30d: 6180, totalSales30d: 1236, lastSync: "15 min atrás", serialNumber: "SN-010" },
  { id: "VM-011", name: "Escritório K1", clientName: "WeWork Faria Lima", location: "12º andar", status: "online", stockLevel: 88, revenue30d: 4560, totalSales30d: 912, lastSync: "1 min atrás", serialNumber: "SN-011" },
  { id: "VM-012", name: "Lounge L1", clientName: "WeWork Faria Lima", location: "Lounge Café", status: "online", stockLevel: 71, revenue30d: 5230, totalSales30d: 1046, lastSync: "3 min atrás", serialNumber: "SN-012" },
];

export const clients: Client[] = [
  { name: "Rodoviária Tietê", machineCount: 3, revenue30d: 22350, totalSales30d: 4470 },
  { name: "Faculdade Anhanguera", machineCount: 2, revenue30d: 5670, totalSales30d: 1134 },
  { name: "Hospital São Luiz", machineCount: 3, revenue30d: 10270, totalSales30d: 2055 },
  { name: "WeWork Faria Lima", machineCount: 2, revenue30d: 9790, totalSales30d: 1958 },
  { name: "Condomínio Alphaville", machineCount: 2, revenue30d: 5310, totalSales30d: 1062 },
];

export const topProducts: Product[] = [
  { name: "Água Mineral 500ml", totalSold: 3240, revenue: 9720, trend: 12.3 },
  { name: "Coca-Cola Lata", totalSold: 2890, revenue: 14450, trend: 8.1 },
  { name: "Barra de Cereal", totalSold: 1670, revenue: 5845, trend: 5.4 },
  { name: "Suco Del Valle", totalSold: 1420, revenue: 7100, trend: -2.1 },
  { name: "Biscoito Oreo", totalSold: 1180, revenue: 4720, trend: 15.7 },
];

export const bottomProducts: Product[] = [
    { name: "Chá Gelado Leão", totalSold: 89, revenue: 356, trend: -28.4 },
    { name: "Amendoim Japonês", totalSold: 112, revenue: 336, trend: -19.2 },
    { name: "Energético Monster", totalSold: 156, revenue: 1248, trend: -12.8 },
    { name: "Paçoca Amor", totalSold: 178, revenue: 356, trend: -8.5 },
    { name: "Vitamina Yakult", totalSold: 203, revenue: 609, trend: -5.1 },
];

export const products: FullProduct[] = [
    {
        id: "p-1",
        code: "BEB-001",
        name: "Coca-Cola Lata",
        description: "Refrigerante de cola 350ml",
        typeId: "pt-1",
        isAlcoholic: false,
        cost: 2.500
    },
    {
        id: "p-2",
        code: "ALC-001",
        name: "Cerveja Heineken",
        description: "Cerveja lager premium 330ml",
        typeId: "pt-1",
        isAlcoholic: true,
        cost: 4.850
    },
    {
        id: "p-3",
        code: "SNA-001",
        name: "Batata Pringles",
        description: "Batata frita sabor original 114g",
        typeId: "pt-8",
        isAlcoholic: false,
        cost: 12.300
    }
];

export const revenueByDay = [
  { day: "Seg", revenue: 2840 },
  { day: "Ter", revenue: 3120 },
  { day: "Qua", revenue: 2960 },
  { day: "Qui", revenue: 3450 },
  { day: "Sex", revenue: 3890 },
  { day: "Sáb", revenue: 4210 },
  { day: "Dom", revenue: 2180 },
];

export const generateHistoricalData = (days: number) => {
  const data = [];
  const today = new Date();
  
  for (let i = days - 1; i >= 0; i--) {
    const d = new Date(today);
    d.setDate(today.getDate() - i);
    
    // Randomize realistic patterns
    // Weekends have slightly different patterns or just variations
    let  baseRevenue = 2000 + Math.random() * 3000;
    if (d.getDay() === 0 || d.getDay() === 6) {
        baseRevenue *= 0.8; // slightly lower revenue on weekends
    }

    const baseProducts = Math.floor(baseRevenue / 10) + Math.floor(Math.random() * 50);
    
    data.push({
      date: d.toLocaleDateString("pt-BR", { day: "2-digit", month: "2-digit" }),
      revenue: Math.round(baseRevenue),
      products: Math.round(baseProducts),
    });
  }
  return data;
};
