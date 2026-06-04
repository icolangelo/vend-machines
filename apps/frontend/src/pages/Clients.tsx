import { SidebarProvider, SidebarTrigger } from "@/components/ui/sidebar";
import { AppSidebar } from "@/components/AppSidebar";
import { KpiCard } from "@/components/dashboard/KpiCard";
import { type Client } from "@/data/mockData";
import { getClients } from "@/lib/api";
import { Users, Box, DollarSign, ShoppingCart, ArrowLeft, ArrowRight, Info, ExternalLink } from "lucide-react";
import { useState, useMemo, useEffect } from "react";
import { useNavigate } from "react-router-dom";
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription } from "@/components/ui/dialog";

export default function Clients() {
    const navigate = useNavigate();
    const [clients, setClients] = useState<Client[]>([]);
    const [loading, setLoading] = useState(true);

    useEffect(() => {
        if (sessionStorage.getItem("isAuthenticated") !== "true") {
            navigate("/");
            return;
        }

        setLoading(true);
        getClients()
            .then(data => {
                setClients(data);
                setLoading(false);
            })
            .catch(err => {
                console.error("Erro ao buscar clientes:", err);
                setLoading(false);
            });
    }, [navigate]);

    const [currentPage, setCurrentPage] = useState(1);
    const [selectedClientModal, setSelectedClientModal] = useState<Client | null>(null);
    const itemsPerPage = 20;

    // Derived KPI states
    const { totalClients, activeMachines, totalRevenue, totalSales } = useMemo(() => {
        const total = clients.length;
        if (total === 0) return { totalClients: 0, activeMachines: 0, totalRevenue: 0, totalSales: 0 };
        
        return {
            totalClients: total,
            activeMachines: clients.reduce((acc, c) => acc + c.machineCount, 0),
            totalRevenue: clients.reduce((acc, c) => acc + c.revenue30d, 0),
            totalSales: clients.reduce((acc, c) => acc + c.totalSales30d, 0)
        };
    }, [clients]);

    // Pagination
    const totalPages = Math.ceil(clients.length / itemsPerPage);
    const paginatedData = useMemo(() => {
        return clients.slice((currentPage - 1) * itemsPerPage, currentPage * itemsPerPage);
    }, [clients, currentPage, itemsPerPage]);

    return (
        <SidebarProvider>
            <div className="min-h-screen flex w-full">
                <AppSidebar />
                <div className="flex-1 flex flex-col min-w-0">
                    <header className="h-12 flex items-center border-b bg-card px-4 gap-3">
                        <SidebarTrigger />
                        <h1 className="text-sm font-semibold text-foreground">Clientes</h1>
                    </header>
                    <main className="flex-1 p-6 space-y-6 overflow-auto">
                        {loading ? (
                            <div className="flex items-center justify-center h-[50vh]">
                                <p className="text-muted-foreground animate-pulse font-medium">Carregando clientes...</p>
                            </div>
                        ) : (
                            <>
                                <div className="flex items-center justify-between">
                            <h2 className="text-lg font-semibold tracking-tight">Visão Geral</h2>
                        </div>

                        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
                            <KpiCard
                                title="Total de Clientes"
                                value={totalClients.toString()}
                                icon={Users}
                                subtitle="Ativos na plataforma"
                            />
                            <KpiCard
                                title="Máquinas Instaladas"
                                value={activeMachines.toString()}
                                icon={Box}
                                subtitle="Em todos os clientes"
                            />
                            <KpiCard
                                title="Faturamento Gerado"
                                value={`R$ ${totalRevenue.toLocaleString("pt-BR")}`}
                                icon={DollarSign}
                                subtitle="Últimos 30 dias"
                            />
                            <KpiCard
                                title="Volume de Vendas"
                                value={totalSales.toLocaleString("pt-BR")}
                                icon={ShoppingCart}
                                subtitle="Produtos vendidos (30d)"
                            />
                        </div>

                        <div className="bg-card border rounded-lg flex flex-col animate-slide-up" style={{ animationDelay: "100ms" }}>
                            <div className="px-5 py-4 border-b flex items-center justify-between gap-4">
                                <h3 className="font-semibold text-foreground flex items-center gap-2">
                                    <Users className="h-4 w-4" /> Relatório de Clientes
                                </h3>
                                <span className="font-mono-data text-xs text-muted-foreground">
                                    {clients.length} itens encontrados
                                </span>
                            </div>

                            <div className="overflow-x-auto">
                                <table className="w-full text-sm">
                                    <thead>
                                        <tr className="border-b bg-muted/50">
                                            <th className="text-left px-5 py-3 text-label font-medium w-3/5">Nome / Empresa</th>
                                            <th className="text-center px-5 py-3 text-label font-medium">Qtd. Máquinas</th>
                                            <th className="text-left px-5 py-3 text-label font-medium">Faturamento (30d)</th>
                                            <th className="text-left px-5 py-3 text-label font-medium">Vendas (30d)</th>
                                            <th className="px-5 py-3 w-12"></th>
                                        </tr>
                                    </thead>
                                    <tbody>
                                        {paginatedData.length === 0 ? (
                                            <tr>
                                                <td colSpan={5} className="px-5 py-8 text-center text-muted-foreground">
                                                    Nenhum cliente cadastrado no momento.
                                                </td>
                                            </tr>
                                        ) : (
                                            paginatedData.map((client, idx) => (
                                                <tr
                                                    key={idx}
                                                    className="border-b last:border-b-0 hover:bg-muted/30 transition-colors duration-150"
                                                >
                                                    <td className="px-5 py-4 font-medium text-foreground">
                                                        <button 
                                                            onClick={() => navigate("/machines", { state: { selectedClient: client.name } })}
                                                            className="flex items-center gap-1.5 text-primary font-semibold hover:underline underline-offset-4 transition-colors text-left"
                                                            title="Ver máquinas deste cliente"
                                                        >
                                                            {client.name}
                                                            <ExternalLink className="w-3.5 h-3.5 opacity-70" />
                                                        </button>
                                                    </td>
                                                    <td className="px-5 py-4 text-center font-mono-data text-muted-foreground">
                                                        {client.machineCount}
                                                    </td>
                                                    <td className="px-5 py-4 font-medium">
                                                        R$ {client.revenue30d.toLocaleString("pt-BR", { minimumFractionDigits: 2 })}
                                                    </td>
                                                    <td className="px-5 py-4 font-mono-data text-muted-foreground">
                                                        {client.totalSales30d.toLocaleString("pt-BR")}
                                                    </td>
                                                    <td className="px-5 py-4 text-right">
                                                        <button 
                                                            onClick={(e) => {
                                                                e.stopPropagation();
                                                                setSelectedClientModal(client);
                                                            }}
                                                            className="p-1.5 text-muted-foreground hover:text-foreground hover:bg-muted rounded transition-colors"
                                                            title="Mais detalhes"
                                                        >
                                                            <Info className="w-4 h-4" />
                                                        </button>
                                                    </td>
                                                </tr>
                                            ))
                                        )}
                                    </tbody>
                                </table>
                            </div>

                            {totalPages > 1 && (
                                <div className="px-5 py-3 border-t flex items-center justify-between">
                                    <span className="text-xs text-muted-foreground">
                                        Página {currentPage} de {totalPages}
                                    </span>
                                    <div className="flex items-center gap-2">
                                        <button
                                            onClick={() => setCurrentPage(p => Math.max(1, p - 1))}
                                            disabled={currentPage === 1}
                                            className="p-1 rounded hover:bg-muted disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                                        >
                                            <ArrowLeft className="w-4 h-4" />
                                        </button>
                                        <button
                                            onClick={() => setCurrentPage(p => Math.min(totalPages, p + 1))}
                                            disabled={currentPage === totalPages}
                                            className="p-1 rounded hover:bg-muted disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                                        >
                                            <ArrowRight className="w-4 h-4" />
                                        </button>
                                    </div>
                                </div>
                            )}
                        </div>
                    </>
                )}
            </main>
                </div>
            </div>

            {/* Modal de Detalhes do Cliente */}
            <Dialog open={!!selectedClientModal} onOpenChange={(open) => !open && setSelectedClientModal(null)}>
                <DialogContent className="max-w-4xl max-h-[90vh] overflow-y-auto">
                    <DialogHeader>
                        <DialogTitle className="text-2xl">{selectedClientModal?.name}</DialogTitle>
                        <DialogDescription>
                            Informações e indicadores comerciais do cliente.
                        </DialogDescription>
                    </DialogHeader>
                    {selectedClientModal && (
                        <div className="space-y-4 py-4">
                            <div className="grid grid-cols-2 md:grid-cols-4 gap-6 bg-muted/30 p-6 rounded-lg border">
                                <div>
                                    <p className="text-xs text-muted-foreground mb-1 uppercase tracking-wider font-semibold">Máquinas Ativas</p>
                                    <p className="font-mono-data font-medium text-lg">{selectedClientModal.machineCount}</p>
                                </div>
                                <div>
                                    <p className="text-xs text-muted-foreground mb-1 uppercase tracking-wider font-semibold">Faturamento</p>
                                    <p className="font-medium text-lg">R$ {selectedClientModal.revenue30d.toLocaleString("pt-BR")}</p>
                                </div>
                                <div>
                                    <p className="text-xs text-muted-foreground mb-1 uppercase tracking-wider font-semibold">Volume de Vendas</p>
                                    <p className="font-medium text-lg">{selectedClientModal.totalSales30d}</p>
                                </div>
                                <div>
                                    <p className="text-xs text-muted-foreground mb-1 uppercase tracking-wider font-semibold">Média por Máquina</p>
                                    <p className="font-medium text-lg">
                                        R$ {selectedClientModal.machineCount > 0 ? (selectedClientModal.revenue30d / selectedClientModal.machineCount).toLocaleString("pt-BR", { maximumFractionDigits: 0 }) : 0}
                                    </p>
                                </div>
                            </div>

                            <div className="mt-8 flex justify-end">
                                <button
                                    onClick={() => {
                                        navigate("/machines", { state: { selectedClient: selectedClientModal.name } });
                                        setSelectedClientModal(null);
                                    }}
                                    className="bg-primary text-primary-foreground hover:bg-primary/90 px-4 py-2 rounded-md text-sm font-medium transition-colors"
                                >
                                    Ver Máquinas deste Cliente
                                </button>
                            </div>
                        </div>
                    )}
                </DialogContent>
            </Dialog>
        </SidebarProvider>
    );
}
