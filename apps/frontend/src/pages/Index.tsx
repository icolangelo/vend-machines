import { SidebarProvider, SidebarTrigger } from "@/components/ui/sidebar";
import { AppSidebar } from "@/components/AppSidebar";
import { KpiCard } from "@/components/dashboard/KpiCard";
import { MachineStatusSummary } from "@/components/dashboard/MachineStatusSummary";
import { MachineAlertTable } from "@/components/dashboard/MachineAlertTable";
import { RevenueChart } from "@/components/dashboard/RevenueChart";
import { ClientRanking } from "@/components/dashboard/ClientRanking";
import { ProductRanking } from "@/components/dashboard/ProductRanking";
import { useEffect } from "react";
import { useNavigate } from "react-router-dom";
import { machines } from "@/data/mockData";
import { DollarSign, ShoppingCart, Box, Zap, LogOut } from "lucide-react";

const totalRevenue = machines.reduce((s, m) => s + m.revenue30d, 0);
const totalSales = machines.reduce((s, m) => s + m.totalSales30d, 0);
const avgStock = Math.round(machines.reduce((s, m) => s + m.stockLevel, 0) / machines.length);
const uptime = Math.round((machines.filter((m) => m.status === "online").length / machines.length) * 100);

export default function Index() {
    const navigate = useNavigate();

    useEffect(() => {
        if (sessionStorage.getItem("isAuthenticated") !== "true") {
            navigate("/");
        }
    }, [navigate]);

    const handleLogout = () => {
        sessionStorage.removeItem("isAuthenticated");
        navigate("/");
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
                        {/* KPI Row */}
                        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
                            <KpiCard
                                title="Faturamento 30d"
                                value={`R$ ${totalRevenue.toLocaleString("pt-BR")}`}
                                icon={DollarSign}
                                trend={{ value: 7.2, label: "vs mês anterior" }}
                                subtitle="vs mês anterior"
                            />
                            <KpiCard
                                title="Vendas 30d"
                                value={totalSales.toLocaleString("pt-BR")}
                                icon={ShoppingCart}
                                trend={{ value: 4.8, label: "" }}
                                subtitle="unidades"
                            />
                            <KpiCard
                                title="Estoque Médio"
                                value={`${avgStock}%`}
                                icon={Box}
                                variant={avgStock < 30 ? "warning" : "default"}
                                subtitle="todas as máquinas"
                            />
                            <KpiCard
                                title="Disponibilidade"
                                value={`${uptime}%`}
                                icon={Zap}
                                variant={uptime < 90 ? "destructive" : "success"}
                                subtitle="máquinas online"
                            />
                        </div>

                        {/* Chart + Status */}
                        <div className="grid grid-cols-1 lg:grid-cols-3 gap-4">
                            <div className="lg:col-span-2">
                                <RevenueChart />
                            </div>
                            <MachineStatusSummary />
                        </div>

                        {/* Alert Table */}
                        <MachineAlertTable />

                        {/* Rankings */}
                        <div className="grid grid-cols-1 lg:grid-cols-3 gap-4">
                            <div className="lg:col-span-1">
                                <ClientRanking />
                            </div>
                            <div className="lg:col-span-2">
                                <ProductRanking />
                            </div>
                        </div>
                    </main>
                </div>
            </div>
        </SidebarProvider>
    );
}
