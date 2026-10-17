import { SidebarProvider, SidebarTrigger } from "@/components/ui/sidebar";
import { AppSidebar } from "@/components/AppSidebar";
import { KpiCard } from "@/components/dashboard/KpiCard";
import { StatusIndicator } from "@/components/dashboard/StatusIndicator";
import { type Machine, type Location } from "@/data/mockData";
import { getMachines, getLocations } from "@/lib/api";
import { Box, Zap, AlertTriangle, ListFilter, ArrowLeft, ArrowRight, Info, Plus, Edit, PackagePlus, RefreshCw, TrendingDown } from "lucide-react";
import { Button } from "@/components/ui/button";
import { useState, useMemo, useEffect, useCallback } from "react";
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
import { getErrorMessage } from "@/lib/utils";
import { useAuth } from "@/contexts/AuthContext";

export default function Machines() {
    const navigate = useNavigate();
    const location = useLocation();
    const { user } = useAuth();
    const canManageMachines = user?.role === "Admin" || user?.role === "PlatformAdmin";
    
    const [machines, setMachines] = useState<Machine[]>([]);
    const [locations, setLocations] = useState<Location[]>([]);
    const [loading, setLoading] = useState(true);
    const [loadError, setLoadError] = useState<string | null>(null);
    const [lastUpdatedAt, setLastUpdatedAt] = useState<string | null>(null);

    const loadMachines = useCallback(async () => {
        setLoading(true);
        setLoadError(null);
        try {
            const [mList, locationList] = await Promise.all([getMachines(), getLocations()]);
            setMachines(mList);
            setLocations(locationList);
            setLastUpdatedAt(new Date().toLocaleTimeString("pt-BR"));
        } catch (err: unknown) {
            console.error("Erro ao carregar máquinas:", err);
            setLoadError(getErrorMessage(err, "Não foi possível carregar as máquinas."));
        } finally {
            setLoading(false);
        }
    }, []);

    useEffect(() => { void loadMachines(); }, [loadMachines]);

    const [selectedLocationId, setSelectedLocationId] = useState<string>(
        location.state?.selectedLocationId || "all"
    );
    const [filterType, setFilterType] = useState<"unavailable" | "lowStock" | "belowTarget" | "all">("all");
    const [currentPage, setCurrentPage] = useState(1);
    const [selectedMachine, setSelectedMachine] = useState<Machine | null>(null);
    
    const itemsPerPage = 20;

    // Derived states
    const filteredMachinesByLocation = useMemo(() => {
        if (selectedLocationId === "all") return machines;
        const selectedLocation = locations.find(item => item.id === selectedLocationId);
        return machines.filter(m => m.locationId === selectedLocationId || (!!selectedLocation && m.clientName === selectedLocation.name));
    }, [machines, locations, selectedLocationId]);

    const { avgStock, uptime, totalCount, attentionCount } = useMemo(() => {
        const total = filteredMachinesByLocation.length;
        if (total === 0) return { avgStock: 0, uptime: 0, totalCount: 0, attentionCount: 0 };
        
        const configuredInventory = filteredMachinesByLocation.filter(m => m.inventoryConfigured);
        const stockSum = configuredInventory.reduce((acc, m) => acc + m.stockLevel, 0);
        const onlineCount = filteredMachinesByLocation.filter(m => m.status === "online").length;
        const alertCount = filteredMachinesByLocation.filter(m => m.status === "offline" || m.status === "warning" || m.status === "unmonitored" || m.status === "inactive" || m.lowStock === true).length;
        
        return {
            avgStock: configuredInventory.length > 0 ? Math.round(stockSum / configuredInventory.length) : 0,
            uptime: Math.round((onlineCount / Math.max(1, filteredMachinesByLocation.filter(m => m.status !== "unmonitored" && m.status !== "inactive").length)) * 100),
            totalCount: total,
            attentionCount: alertCount
        };
    }, [filteredMachinesByLocation]);

    // Table view logic
    const tableData = useMemo(() => {
        if (filterType === "unavailable") {
            return filteredMachinesByLocation.filter(m => m.status === "offline" || m.status === "warning" || m.status === "unmonitored" || m.status === "inactive");
        }
        if (filterType === "lowStock") return filteredMachinesByLocation.filter(m => m.lowStock === true);
        if (filterType === "belowTarget") return filteredMachinesByLocation.filter(m => m.belowSalesTarget === true);
        return filteredMachinesByLocation;
    }, [filteredMachinesByLocation, filterType]);

    // Pagination
    const totalPages = Math.ceil(tableData.length / itemsPerPage);
    const paginatedData = useMemo(() => {
        return tableData.slice((currentPage - 1) * itemsPerPage, currentPage * itemsPerPage);
    }, [tableData, currentPage, itemsPerPage]);

    // Reset page when switching filters or client
    useEffect(() => {
        setCurrentPage(1);
    }, [selectedLocationId, filterType]);

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
                            {loading && !lastUpdatedAt ? (
                                <div className="flex items-center justify-center h-[50vh]">
                                    <p className="text-muted-foreground animate-pulse font-medium">Carregando máquinas...</p>
                                </div>
                            ) : loadError && !lastUpdatedAt ? (
                                <div className="flex flex-col items-center justify-center gap-3 min-h-[40vh] rounded-lg border border-dashed bg-card p-6 text-center">
                                    <AlertTriangle className="h-9 w-9 text-destructive" />
                                    <div><h2 className="font-semibold">Não foi possível carregar as máquinas</h2><p className="mt-1 text-sm text-muted-foreground">{loadError}</p></div>
                                    <Button variant="outline" onClick={() => void loadMachines()}><RefreshCw className="mr-2 h-4 w-4" />Tentar novamente</Button>
                                </div>
                            ) : (
                            <>
                                {(loadError || loading) && lastUpdatedAt && (
                                    <div role={loadError ? "alert" : "status"} className="flex flex-wrap items-center justify-between gap-3 rounded-md border border-amber-300 bg-amber-50 p-3 text-sm text-amber-950">
                                        <span>{loadError ? `Não foi possível atualizar as máquinas e localizações. Os dados exibidos podem estar desatualizados. ${loadError}` : "Atualizando máquinas e localizações... Os dados da última leitura continuam visíveis."}</span>
                                        {loadError && <Button variant="outline" size="sm" onClick={() => void loadMachines()} disabled={loading}><RefreshCw className="mr-2 h-4 w-4" />Tentar novamente</Button>}
                                    </div>
                                )}
                                <div className="flex items-center justify-between">
                            <div className="flex items-center gap-4">
                                <h2 className="text-lg font-semibold tracking-tight">Visão Geral</h2>
                                {canManageMachines && <Button onClick={() => navigate("/machines/new")} size="sm" className="gap-2">
                                    <Plus className="w-4 h-4" />
                                    Nova Máquina
                                </Button>}
                            </div>
                            <div className="flex flex-col sm:flex-row gap-2">
                                <Button variant="outline" size="sm" onClick={() => void loadMachines()} disabled={loading}>
                                    <RefreshCw className={`mr-2 h-4 w-4 ${loading ? "animate-spin" : ""}`} />Atualizar
                                </Button>
                                <div className="w-full sm:w-[280px]">
                                    <Select value={selectedLocationId} onValueChange={setSelectedLocationId}>
                                        <SelectTrigger>
                                            <SelectValue placeholder="Selecione uma localização" />
                                        </SelectTrigger>
                                        <SelectContent>
                                            <SelectItem value="all">Todas as Localizações</SelectItem>
                                            {locations.filter(l => l.id && l.id.trim() !== "").map(location => (
                                                <SelectItem key={location.id} value={location.id}>
                                                    {location.name}
                                                </SelectItem>
                                            ))}
                                        </SelectContent>
                                    </Select>
                                </div>
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
                                <Tabs value={filterType} onValueChange={(v) => setFilterType(v as "unavailable" | "lowStock" | "belowTarget" | "all")}>
                                    <TabsList className="h-auto flex-wrap">
                                        <TabsTrigger value="unavailable" className="gap-2">
                                            <AlertTriangle className="h-4 w-4" />
                                            Indisponíveis ou atenção
                                        </TabsTrigger>
                                        <TabsTrigger value="lowStock" className="gap-2">
                                            <PackagePlus className="h-4 w-4" />
                                            Reposição
                                        </TabsTrigger>
                                        <TabsTrigger value="belowTarget" className="gap-2">
                                            <TrendingDown className="h-4 w-4" />
                                            Abaixo da meta
                                        </TabsTrigger>
                                        <TabsTrigger value="all" className="gap-2">
                                            <ListFilter className="h-4 w-4" />
                                            Todas as Máquinas
                                        </TabsTrigger>
                                    </TabsList>
                                </Tabs>
                                <span className="font-mono-data text-xs text-muted-foreground self-start sm:self-auto">
                                    {tableData.length} itens · consulta às {lastUpdatedAt ?? "—"}
                                </span>
                            </div>

                            <div className="overflow-x-auto">
                                <table className="w-full text-sm">
                                    <thead>
                                        <tr className="border-b bg-muted/50">
                                            <th className="text-left px-5 py-2.5 text-label font-medium">ID</th>
                                            <th className="text-left px-5 py-2.5 text-label font-medium">Máquina</th>
                                            <th className="text-left px-5 py-2.5 text-label font-medium">Localização</th>
                                            <th className="text-left px-5 py-2.5 text-label font-medium">Status</th>
                                            <th className="text-left px-5 py-2.5 text-label font-medium">Estoque</th>
                                            <th className="text-left px-5 py-2.5 text-label font-medium">Vendas / meta 30 dias</th>
                                            <th className="text-left px-5 py-2.5 text-label font-medium">Última Sinc.</th>
                                            <th className="px-5 py-2.5 w-12"></th>
                                        </tr>
                                    </thead>
                                    <tbody>
                                        {paginatedData.length === 0 ? (
                                            <tr>
                                                <td colSpan={8} className="px-5 py-8 text-center text-muted-foreground">
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
                                                        {machine.inventoryConfigured ? <div className="flex items-center gap-2">
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
                                                        </div> : <span className="text-xs text-muted-foreground">Não cadastrado</span>}
                                                    </td>
                                                    <td className="px-5 py-3 text-xs">
                                                        {machine.minimumSalesTarget30d != null ? (
                                                            <div>
                                                                <span className="font-mono-data">{machine.totalSales30d} / {machine.minimumSalesTarget30d}</span>
                                                                {machine.belowSalesTarget && <span className="block text-destructive">Abaixo da meta</span>}
                                                            </div>
                                                        ) : <span className="text-muted-foreground">Sem meta</span>}
                                                    </td>
                                                    <td className="px-5 py-3 text-xs text-muted-foreground">
                                                        {machine.lastSync}
                                                    </td>
                                                    <td className="px-5 py-3 text-right">
                                                        <div className="flex items-center justify-end gap-1">
                                                            {canManageMachines && <button
                                                                onClick={(e) => {
                                                                    e.stopPropagation();
                                                                    if (machine.id) {
                                                                        navigate(`/machines/${encodeURIComponent(machine.id)}/edit`);
                                                                    }
                                                                }}
                                                                className="p-1.5 text-muted-foreground hover:text-foreground hover:bg-muted rounded transition-colors"
                                                                title="Editar Equipamento"
                                                            >
                                                                <Edit className="w-4 h-4" />
                                                            </button>}
                                                            {machine.lowStock && <button
                                                                onClick={(e) => {
                                                                    e.stopPropagation();
                                                                    navigate(`/inventory?machineId=${encodeURIComponent(machine.id)}`);
                                                                }}
                                                                className="p-1.5 text-amber-700 hover:bg-amber-100 rounded transition-colors"
                                                                title="Abrir estoque desta máquina"
                                                                aria-label={`Abrir estoque de ${machine.name}`}
                                                            >
                                                                <PackagePlus className="w-4 h-4" />
                                                            </button>}
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
                                    if (selectedMachine?.id) {
                                        navigate(`/machines/${encodeURIComponent(selectedMachine.id)}/edit`);
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
                                    <p className="text-xs text-muted-foreground mb-1 uppercase tracking-wider font-semibold">Número de série</p>
                                    <p className="font-mono-data font-medium text-lg">{selectedMachine.serialNumber || "-"}</p>
                                </div>
                                <div>
                                    <p className="text-xs text-muted-foreground mb-1 uppercase tracking-wider font-semibold">Nome</p>
                                    <p className="font-medium text-lg">{selectedMachine.name}</p>
                                </div>
                                <div>
                                    <p className="text-xs text-muted-foreground mb-1 uppercase tracking-wider font-semibold">Localização</p>
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
