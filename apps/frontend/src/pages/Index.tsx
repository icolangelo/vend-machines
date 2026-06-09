import { SidebarProvider, SidebarTrigger } from "@/components/ui/sidebar";
import { AppSidebar } from "@/components/AppSidebar";
import { KpiCard } from "@/components/dashboard/KpiCard";
import { MachineStatusSummary } from "@/components/dashboard/MachineStatusSummary";
import { MachineAlertTable } from "@/components/dashboard/MachineAlertTable";
import { RevenueChart } from "@/components/dashboard/RevenueChart";
import { ClientRanking } from "@/components/dashboard/ClientRanking";
import { ProductRanking } from "@/components/dashboard/ProductRanking";
import { useEffect, useState, useMemo } from "react";
import { useNavigate } from "react-router-dom";
import { getMachines, getClients, getTopProducts, getBottomProducts, getDashboardStats } from "@/lib/api";
import { DollarSign, ShoppingCart, Box, Zap, LogOut } from "lucide-react";
import { useAuth } from "@/contexts/AuthContext";

export default function Index() {
    const navigate = useNavigate();
    const { logout } = useAuth();
    const [machinesList, setMachinesList] = useState<any[]>([]);
    const [clientsList, setClientsList] = useState<any[]>([]);
    const [topProductsList, setTopProductsList] = useState<any[]>([]);
    const [bottomProductsList, setBottomProductsList] = useState<any[]>([]);
    const [stats, setStats] = useState<any>(null);
    const [loading, setLoading] = useState(true);

    useEffect(() => {
        if (sessionStorage.getItem("isAuthenticated") !== "true") {
            navigate("/");
            return;
        }

        setLoading(true);
        Promise.all([
            getMachines(),
            getClients(),
            getTopProducts(),
            getBottomProducts(),
            getDashboardStats()
        ]).then(([mList, cList, topList, bottomList, dashboardStats]) => {
            setMachinesList(mList);
            setClientsList(cList);
            setTopProductsList(topList);
            setBottomProductsList(bottomList);
            setStats(dashboardStats);
            setLoading(false);
        }).catch(err => {
            console.error("Erro ao carregar dados do dashboard:", err);
            setLoading(false);
        });
    }, [navigate]);

    const kpis = useMemo(() => {
        if (loading || !stats) {
            return { totalRevenue: 0, totalSales: 0, avgStock: 0, uptime: 0 };
        }
        const mLength = machinesList.length || 1;
        const avgSt = Math.round(machinesList.reduce((s, m) => s + m.stockLevel, 0) / mLength);
        const onlineCount = machinesList.filter((m) => m.status === "online").length;
        const upt = Math.round((onlineCount / mLength) * 100);

        return {
            totalRevenue: stats.totalRevenue30d || 0,
            totalSales: stats.totalSales30d || 0,
            avgStock: avgSt,
            uptime: upt
        };
    }, [machinesList, stats, loading]);

    const handleLogout = () => {
        logout();
    };

    return (
        <SidebarProvider>
            <div className="min-h-screen flex w-full">
                <AppSidebar />
                <div className="flex-1 flex flex-col min-w-0">
                    <header className="h-12 flex items-center border-b bg-card px-4 gap-3">
                        <SidebarTrigger />
                        <h1 className="text-sm font-semibold text-foreground">Dashboard</h1>
                        <div className="ml-auto flex items-center gap-4">
                            <span className="text-xs text-muted-foreground">
                                Última atualização: agora
                            </span>
                            <button
                                onClick={handleLogout}
                                className="text-muted-foreground hover:text-foreground transition-colors flex items-center gap-2"
                                title="Sair"
                            >
                                <LogOut className="w-4 h-4" />
                            </button>
                        </div>
                    </header>

                    <main className="flex-1 p-6 space-y-6 overflow-auto">
                        {loading ? (
                            <div className="flex items-center justify-center h-[50vh]">
                                <p className="text-muted-foreground animate-pulse font-medium">Carregando painel...</p>
                            </div>
                        ) : (
                            <>
                                {/* KPI Row */}
                                <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
                                    <KpiCard
                                        title="Faturamento 30d"
                                        value={`R$ ${kpis.totalRevenue.toLocaleString("pt-BR")}`}
                                        icon={DollarSign}
                                        trend={{ value: 7.2, label: "vs mês anterior" }}
                                        subtitle="vs mês anterior"
                                    />
                                    <KpiCard
                                        title="Vendas 30d"
                                        value={kpis.totalSales.toLocaleString("pt-BR")}
                                        icon={ShoppingCart}
                                        trend={{ value: 4.8, label: "" }}
                                        subtitle="unidades"
                                    />
                                    <KpiCard
                                        title="Estoque Médio"
                                        value={`${kpis.avgStock}%`}
                                        icon={Box}
                                        variant={kpis.avgStock < 30 ? "warning" : "default"}
                                        subtitle="todas as máquinas"
                                    />
                                    <KpiCard
                                        title="Disponibilidade"
                                        value={`${kpis.uptime}%`}
                                        icon={Zap}
                                        variant={kpis.uptime < 90 ? "destructive" : "success"}
                                        subtitle="máquinas online"
                                    />
                                </div>

                                {/* Chart + Status */}
                                <div className="grid grid-cols-1 lg:grid-cols-3 gap-4">
                                    <div className="lg:col-span-2">
                                        <RevenueChart />
                                    </div>
                                    <MachineStatusSummary machines={machinesList} />
                                </div>

                                {/* Alert Table */}
                                <MachineAlertTable machines={machinesList} />

                                {/* Rankings */}
                                <div className="grid grid-cols-1 lg:grid-cols-3 gap-4">
                                    <div className="lg:col-span-1">
                                        <ClientRanking clients={clientsList} />
                                    </div>
                                    <div className="lg:col-span-2">
                                        <ProductRanking topProducts={topProductsList} bottomProducts={bottomProductsList} />
                                    </div>
                                </div>
                            </>
                        )}
                    </main>
                </div>
            </div>
        </SidebarProvider>
    );
}
