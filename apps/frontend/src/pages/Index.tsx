import { SidebarProvider, SidebarTrigger } from "@/components/ui/sidebar";
import { AppSidebar } from "@/components/AppSidebar";
import { KpiCard } from "@/components/dashboard/KpiCard";
import { MachineStatusSummary } from "@/components/dashboard/MachineStatusSummary";
import { MachineAlertTable } from "@/components/dashboard/MachineAlertTable";
import { RevenueChart } from "@/components/dashboard/RevenueChart";
import { ClientRanking } from "@/components/dashboard/ClientRanking";
import { ProductRanking } from "@/components/dashboard/ProductRanking";
import { useCallback, useEffect, useMemo, useState } from "react";
import { getMachines, getLocations, getTopProducts, getBottomProducts, getDashboardStats, type DashboardStats } from "@/lib/api";
import type { Location, Machine, Product } from "@/data/mockData";
import { DollarSign, ShoppingCart, Box, Zap, LogOut, RefreshCw } from "lucide-react";
import { useAuth } from "@/contexts/AuthContext";
import { Button } from "@/components/ui/button";

export default function Index() {
    const { logout } = useAuth();
    const [machinesList, setMachinesList] = useState<Machine[]>([]);
    const [clientsList, setClientsList] = useState<Location[]>([]);
    const [topProductsList, setTopProductsList] = useState<Product[]>([]);
    const [bottomProductsList, setBottomProductsList] = useState<Product[]>([]);
    const [stats, setStats] = useState<DashboardStats | null>(null);
    const [loading, setLoading] = useState(true);
    const [loadError, setLoadError] = useState(false);
    const [lastUpdatedAt, setLastUpdatedAt] = useState<Date | null>(null);
    const [refreshVersion, setRefreshVersion] = useState(0);

    const loadDashboard = useCallback(async () => {
        setLoading(true);
        setLoadError(false);
        try {
            const [mList, cList, topList, bottomList, dashboardStats] = await Promise.all([
                getMachines(),
                getLocations(),
                getTopProducts(),
                getBottomProducts(),
                getDashboardStats()
            ]);
            setMachinesList(mList);
            setClientsList(cList);
            setTopProductsList(topList);
            setBottomProductsList(bottomList);
            setStats(dashboardStats);
            setLastUpdatedAt(new Date());
        } catch (err) {
            console.error("Erro ao carregar dados do dashboard:", err);
            setLoadError(true);
        } finally {
            setLoading(false);
        }
    }, []);

    useEffect(() => {
        void loadDashboard();
    }, [loadDashboard, refreshVersion]);

    const kpis = useMemo(() => {
        if (loading || !stats) {
            return { totalRevenue: 0, totalSales: 0, avgStock: 0, uptime: 0 };
        }
        const monitoredMachines = machinesList.filter((machine) => machine.status !== "unmonitored" && machine.status !== "inactive");
        const hasMachines = monitoredMachines.length > 0;
        const mLength = hasMachines ? monitoredMachines.length : 1;
        const stockMachines = machinesList.filter((machine) => machine.inventoryConfigured);
        const avgSt = stockMachines.length > 0 ? Math.round(stockMachines.reduce((sum, machine) => sum + machine.stockLevel, 0) / stockMachines.length) : 0;
        const onlineCount = monitoredMachines.filter((m) => m.status === "online").length;
        const upt = hasMachines ? Math.round((onlineCount / mLength) * 100) : 0;

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
                        <h1 className="text-sm font-semibold text-foreground">Painel</h1>
                        <div className="ml-auto flex items-center gap-4">
                            <span className="text-xs text-muted-foreground">
                                Última atualização: {lastUpdatedAt ? lastUpdatedAt.toLocaleTimeString("pt-BR") : "—"}
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
                        {loading && !lastUpdatedAt ? (
                            <div className="flex items-center justify-center h-[50vh]">
                                <p className="text-muted-foreground animate-pulse font-medium">Carregando painel...</p>
                            </div>
                        ) : loadError && !lastUpdatedAt ? (
                            <div role="alert" className="flex min-h-[50vh] flex-col items-center justify-center gap-3 text-center">
                                <p className="font-medium">Não foi possível carregar os dados do painel.</p>
                                <p className="text-sm text-muted-foreground">Confira sua conexão e tente novamente.</p>
                                <Button onClick={() => setRefreshVersion(version => version + 1)}>
                                    <RefreshCw className="mr-2 h-4 w-4" />
                                    Tentar novamente
                                </Button>
                            </div>
                        ) : (
                            <>
                                {loadError && (
                                    <div role="alert" className="flex flex-wrap items-center justify-between gap-3 rounded-md border border-destructive/30 bg-destructive/5 p-3 text-sm">
                                        <span>Não foi possível atualizar o painel. Os dados exibidos são da última carga concluída.</span>
                                        <Button variant="outline" size="sm" onClick={() => setRefreshVersion(version => version + 1)}>
                                            <RefreshCw className="mr-2 h-4 w-4" />
                                            Tentar novamente
                                        </Button>
                                    </div>
                                )}
                                {loading && lastUpdatedAt && <p className="text-xs text-muted-foreground">Atualizando painel...</p>}
                                {/* KPI Row */}
                                <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
                                    <KpiCard
                                        title="Receita após estornos 30d"
                                        value={`R$ ${kpis.totalRevenue.toLocaleString("pt-BR")}`}
                                        icon={DollarSign}
                                        subtitle="antes da taxa da plataforma"
                                    />
                                    <KpiCard
                                        title="Unidades entregues 30d"
                                        value={kpis.totalSales.toLocaleString("pt-BR")}
                                        icon={ShoppingCart}
                                        subtitle="entregas confirmadas pela máquina"
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
                                        <RevenueChart data={stats?.performanceHistory ?? []} />
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
