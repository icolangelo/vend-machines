import { SidebarProvider, SidebarTrigger } from "@/components/ui/sidebar";
import { AppSidebar } from "@/components/AppSidebar";
import { KpiCard } from "@/components/dashboard/KpiCard";
import { StatusIndicator } from "@/components/dashboard/StatusIndicator";
import { type Machine, type Client } from "@/data/mockData";
import { getMachines, getClients } from "@/lib/api";
import { Box, Zap, AlertTriangle, ListFilter, ArrowLeft, ArrowRight, Info, Plus, Edit } from "lucide-react";
import { Button } from "@/components/ui/button";
import { useState, useMemo, useEffect } from "react";
import { useNavigate, useLocation } from "react-router-dom";
import {
    Select,
    SelectContent,
    SelectItem,
    SelectTrigger,
    SelectValue,
} from "@/components/ui/select";
import { Tabs, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription } from "@/components/ui/dialog";

export default function Machines() {
    const navigate = useNavigate();
    const location = useLocation();
    
    const [machines, setMachines] = useState<Machine[]>([]);
    const [clients, setClients] = useState<Client[]>([]);
    const [loading, setLoading] = useState(true);

    useEffect(() => {
        if (sessionStorage.getItem("isAuthenticated") !== "true") {
            navigate("/");
            return;
        }

        setLoading(true);
        Promise.all([getMachines(), getClients()])
            .then(([mList, cList]) => {
                setMachines(mList);
                setClients(cList);
                setLoading(false);
            })
            .catch(err => {
                console.error("Erro ao carregar máquinas:", err);
                setLoading(false);
            });
    }, [navigate]);

    const [selectedClient, setSelectedClient] = useState<string>(
        location.state?.selectedClient || "all"
    );
    const [filterType, setFilterType] = useState<"attention" | "all">("all");
    const [currentPage, setCurrentPage] = useState(1);
    const [selectedMachine, setSelectedMachine] = useState<Machine | null>(null);
    
    const itemsPerPage = 20;

    // Derived states
    const filteredMachinesByClient = useMemo(() => {
        if (selectedClient === "all") return machines;
        return machines.filter(m => m.clientName === selectedClient);
    }, [machines, selectedClient]);

    const { avgStock, uptime, totalCount, attentionCount } = useMemo(() => {
        const total = filteredMachinesByClient.length;
        if (total === 0) return { avgStock: 0, uptime: 0, totalCount: 0, attentionCount: 0 };
        
        const stockSum = filteredMachinesByClient.reduce((acc, m) => acc + m.stockLevel, 0);
        const onlineCount = filteredMachinesByClient.filter(m => m.status === "online").length;
        const alertCount = filteredMachinesByClient.filter(m => m.status === "offline" || m.status === "warning" || m.stockLevel < 25).length;
        
        return {
            avgStock: Math.round(stockSum / total),
            uptime: Math.round((onlineCount / total) * 100),
            totalCount: total,
            attentionCount: alertCount
        };
    }, [filteredMachinesByClient]);

    // Table view logic
    const tableData = useMemo(() => {
        if (filterType === "attention") {
            return filteredMachinesByClient.filter(m => m.status === "offline" || m.status === "warning" || m.stockLevel < 25);
        }
        return filteredMachinesByClient;
    }, [filteredMachinesByClient, filterType]);

    // Pagination
    const totalPages = Math.ceil(tableData.length / itemsPerPage);
    const paginatedData = useMemo(() => {
        if (filterType === "attention") {
            return tableData; // Usually attention table is just a summary list without pagination in dashboard, but following the prompt: pagination mainly for all.
        }
        return tableData.slice((currentPage - 1) * itemsPerPage, currentPage * itemsPerPage);
    }, [tableData, filterType, currentPage, itemsPerPage]);

    // Reset page when switching filters or client
    useEffect(() => {
        setCurrentPage(1);
    }, [selectedClient, filterType]);

    return (
        <SidebarProvider>
            <div className="min-h-screen flex w-full">
                <AppSidebar />
                <div className="flex-1 flex flex-col min-w-0">
                    <header className="h-12 flex items-center border-b bg-card px-4 gap-3">
                        <SidebarTrigger />
                        <h1 className="text-sm font-semibold text-foreground">Máquinas</h1>
                    </header>
                    <main className="flex-1 p-6 space-y-6 overflow-auto">
                        {loading ? (
                            <div className="flex items-center justify-center h-[50vh]">
                                <p className="text-muted-foreground animate-pulse font-medium">Carregando máquinas...</p>
                            </div>
                        ) : (
                            <>
                                <div className="flex items-center justify-between">
                            <div className="flex items-center gap-4">
                                <h2 className="text-lg font-semibold tracking-tight">Visão Geral</h2>
                                <Button onClick={() => navigate("/machines/new")} size="sm" className="gap-2">
                                    <Plus className="w-4 h-4" />
                                    Nova Máquina
                                </Button>
                            </div>
                            <div className="w-[280px]">
                                <Select value={selectedClient} onValueChange={setSelectedClient}>
                                    <SelectTrigger>
                                        <SelectValue placeholder="Selecione um cliente" />
                                    </SelectTrigger>
                                    <SelectContent>
                                        <SelectItem value="all">Todos os Clientes</SelectItem>
                                        {clients.map(client => (
                                            <SelectItem key={client.name} value={client.name}>
                                                {client.name}
                                            </SelectItem>
                                        ))}
                                    </SelectContent>
                                </Select>
                            </div>
                        </div>

                        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
                            <KpiCard
                                title="Estoque Total Médio"
                                value={`${avgStock}%`}
                                icon={Box}
                                variant={avgStock < 30 ? "warning" : "default"}
                                subtitle="capacidade atual"
                            />
                            <KpiCard
                                title="Disponibilidade"
                                value={`${uptime}%`}
                                icon={Zap}
                                variant={uptime < 90 ? "destructive" : "success"}
                                subtitle="máquinas online"
                            />
                            <KpiCard
                                title="Total de Máquinas"
                                value={totalCount.toString()}
                                icon={Box}
                                subtitle="cadastradas"
                            />
                            <KpiCard
                                title="Status Offline ou Atenção"
                                value={attentionCount.toString()}
                                icon={AlertTriangle}
                                variant={attentionCount > 0 ? "destructive" : "success"}
                                subtitle="requerem verificação"
                            />
                        </div>

                        <div className="bg-card border rounded-lg flex flex-col animate-slide-up" style={{ animationDelay: "100ms" }}>
                            <div className="px-5 py-4 border-b flex flex-col sm:flex-row sm:items-center gap-4 justify-between">
                                <Tabs value={filterType} onValueChange={(v) => setFilterType(v as "attention" | "all")}>
                                    <TabsList>
                                        <TabsTrigger value="attention" className="gap-2">
                                            <AlertTriangle className="h-4 w-4" />
                                            Requerem Atenção
                                        </TabsTrigger>
                                        <TabsTrigger value="all" className="gap-2">
                                            <ListFilter className="h-4 w-4" />
                                            Todas as Máquinas
                                        </TabsTrigger>
                                    </TabsList>
                                </Tabs>
                                <span className="font-mono-data text-xs text-muted-foreground self-start sm:self-auto">
                                    {tableData.length} itens encontrados
                                </span>
                            </div>

                            <div className="overflow-x-auto">
                                <table className="w-full text-sm">
                                    <thead>
                                        <tr className="border-b bg-muted/50">
                                            <th className="text-left px-5 py-2.5 text-label font-medium">ID</th>
                                            <th className="text-left px-5 py-2.5 text-label font-medium">Máquina</th>
                                            <th className="text-left px-5 py-2.5 text-label font-medium">Cliente</th>
                                            <th className="text-left px-5 py-2.5 text-label font-medium">Status</th>
                                            <th className="text-left px-5 py-2.5 text-label font-medium">Estoque</th>
                                            <th className="text-left px-5 py-2.5 text-label font-medium">Última Sinc.</th>
                                            <th className="px-5 py-2.5 w-12"></th>
                                        </tr>
                                    </thead>
                                    <tbody>
                                        {paginatedData.length === 0 ? (
                                            <tr>
                                                <td colSpan={7} className="px-5 py-8 text-center text-muted-foreground">
                                                    Nenhuma máquina encontrada para os filtros selecionados.
                                                </td>
                                            </tr>
                                        ) : (
                                            paginatedData.map((machine) => (
                                                <tr
                                                    key={machine.id}
                                                    className="border-b last:border-b-0 hover:bg-muted/30 transition-colors duration-150 cursor-pointer"
                                                >
                                                    <td className="px-5 py-3 font-mono-data text-xs text-muted-foreground">
                                                        {machine.id}
                                                    </td>
                                                    <td className="px-5 py-3 font-medium text-foreground">
                                                        {machine.name}
                                                    </td>
                                                    <td className="px-5 py-3 text-muted-foreground">
                                                        {machine.clientName}
                                                    </td>
                                                    <td className="px-5 py-3">
                                                        <StatusIndicator status={machine.status} />
                                                    </td>
                                                    <td className="px-5 py-3">
                                                        <div className="flex items-center gap-2">
                                                            <div className="w-16 h-1.5 bg-muted rounded-full overflow-hidden">
                                                                <div
                                                                    className={`h-full rounded-full ${machine.stockLevel > 50
                                                                            ? "bg-success"
                                                                            : machine.stockLevel > 20
                                                                                ? "bg-warning"
                                                                                : "bg-destructive"
                                                                        }`}
                                                                    style={{ width: `${machine.stockLevel}%` }}
                                                                />
                                                            </div>
                                                            <span className="font-mono-data text-xs">{machine.stockLevel}%</span>
                                                        </div>
                                                    </td>
                                                    <td className="px-5 py-3 text-xs text-muted-foreground">
                                                        {machine.lastSync}
                                                    </td>
                                                    <td className="px-5 py-3 text-right">
                                                        <div className="flex items-center justify-end gap-1">
                                                            <button 
                                                                onClick={(e) => {
                                                                    e.stopPropagation();
                                                                    navigate(`/machines/${machine.id}/edit`);
                                                                }}
                                                                className="p-1.5 text-muted-foreground hover:text-foreground hover:bg-muted rounded transition-colors"
                                                                title="Editar Equipamento"
                                                            >
                                                                <Edit className="w-4 h-4" />
                                                            </button>
                                                            <button 
                                                                onClick={(e) => {
                                                                    e.stopPropagation();
                                                                    setSelectedMachine(machine);
                                                                }}
                                                                className="p-1.5 text-muted-foreground hover:text-foreground hover:bg-muted rounded transition-colors"
                                                                title="Mais detalhes"
                                                            >
                                                                <Info className="w-4 h-4" />
                                                            </button>
                                                        </div>
                                                    </td>
                                                </tr>
                                            ))
                                        )}
                                    </tbody>
                                </table>
                            </div>

                            {filterType === "all" && totalPages > 1 && (
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

            {/* Modal de Detalhes da Máquina */}
            <Dialog open={!!selectedMachine} onOpenChange={(open) => !open && setSelectedMachine(null)}>
                <DialogContent className="max-w-4xl max-h-[90vh] overflow-y-auto">
                    <DialogHeader>
                        <div className="flex items-start justify-between pr-6">
                            <div>
                                <DialogTitle className="text-2xl">{selectedMachine?.name}</DialogTitle>
                                <DialogDescription>
                                    Informações e indicadores completos do equipamento.
                                </DialogDescription>
                            </div>
                            <Button 
                                variant="outline" 
                                size="sm" 
                                className="gap-2"
                                onClick={() => {
                                    if (selectedMachine) {
                                        navigate(`/machines/${selectedMachine.id}/edit`);
                                    }
                                }}
                            >
                                <Edit className="w-4 h-4" />
                                Editar Configurações
                            </Button>
                        </div>
                    </DialogHeader>
                    {selectedMachine && (
                        <div className="space-y-4 py-4">
                            <div className="grid grid-cols-2 md:grid-cols-6 gap-6 bg-muted/30 p-6 rounded-lg border">
                                <div>
                                    <p className="text-xs text-muted-foreground mb-1 uppercase tracking-wider font-semibold">ID da Máquina</p>
                                    <p className="font-mono-data font-medium text-lg">{selectedMachine.id}</p>
                                </div>
                                <div>
                                    <p className="text-xs text-muted-foreground mb-1 uppercase tracking-wider font-semibold">Serial Number</p>
                                    <p className="font-mono-data font-medium text-lg">{selectedMachine.serialNumber || "-"}</p>
                                </div>
                                <div>
                                    <p className="text-xs text-muted-foreground mb-1 uppercase tracking-wider font-semibold">Nome</p>
                                    <p className="font-medium text-lg">{selectedMachine.name}</p>
                                </div>
                                <div>
                                    <p className="text-xs text-muted-foreground mb-1 uppercase tracking-wider font-semibold">Cliente</p>
                                    <p className="font-medium text-sm">{selectedMachine.clientName}</p>
                                </div>
                                <div>
                                    <p className="text-xs text-muted-foreground mb-1 uppercase tracking-wider font-semibold">Localização</p>
                                    <p className="font-medium text-base">{selectedMachine.location}</p>
                                </div>
                                <div>
                                    <p className="text-xs text-muted-foreground mb-1 uppercase tracking-wider font-semibold">Mercado Pago</p>
                                    <p className="font-medium text-sm">
                                        {selectedMachine.mercadoPagoEnabled ? (
                                            <span className="text-green-600 font-semibold flex items-center gap-1"><Zap className="w-3.5 h-3.5" /> Ativo</span>
                                        ) : (
                                            <span className="text-muted-foreground">Inativo</span>
                                        )}
                                    </p>
                                </div>
                            </div>

                            <div className="grid grid-cols-1 md:grid-cols-2 gap-6 mt-6">
                                <div className="p-6 border rounded-lg bg-card">
                                    <h3 className="text-sm font-semibold mb-4 text-muted-foreground">Status Atual</h3>
                                    <div className="flex items-center gap-4">
                                        <StatusIndicator status={selectedMachine.status} />
                                        <span className="text-sm text-muted-foreground">
                                            Última leitura: {selectedMachine.lastSync}
                                        </span>
                                    </div>
                                </div>

                                <div className="p-6 border rounded-lg bg-card">
                                    <h3 className="text-sm font-semibold mb-4 text-muted-foreground">Nível de Estoque</h3>
                                    <div className="flex flex-col gap-2">
                                        <div className="flex justify-between items-center text-sm">
                                            <span className="font-medium">Capacidade Preenchida</span>
                                            <span className="font-mono-data font-bold">{selectedMachine.stockLevel}%</span>
                                        </div>
                                        <div className="w-full h-3 bg-muted rounded-full overflow-hidden">
                                            <div
                                                className={`h-full rounded-full transition-all duration-500 ease-in-out ${
                                                    selectedMachine.stockLevel > 50
                                                        ? "bg-success"
                                                        : selectedMachine.stockLevel > 20
                                                            ? "bg-warning"
                                                            : "bg-destructive"
                                                }`}
                                                style={{ width: `${selectedMachine.stockLevel}%` }}
                                            />
                                        </div>
                                    </div>
                                </div>
                            </div>
                        </div>
                    )}
                </DialogContent>
            </Dialog>
        </SidebarProvider>
    );
}
