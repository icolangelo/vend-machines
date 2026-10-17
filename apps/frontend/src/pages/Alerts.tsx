import { SidebarProvider, SidebarTrigger } from "@/components/ui/sidebar";
import { AppSidebar } from "@/components/AppSidebar";
import { useState, useEffect, useMemo, useCallback } from "react";
import { Input } from "@/components/ui/input";
import { Button } from "@/components/ui/button";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Bell, Search, AlertTriangle, AlertCircle, Calendar, Users, RefreshCw, Package } from "lucide-react";
import { type Machine } from "@/data/mockData";
import { getMachines, getOperationalAlerts, markOperationalAlertRead, type OperationalAlertItem } from "@/lib/api";
import { filterOperationalAlerts, type OperationalAlertFilter } from "@/lib/operationalAlertFilters";
import { StatusIndicator } from "@/components/dashboard/StatusIndicator";
import { toast } from "sonner";

export default function Alerts() {
    const [machines, setMachines] = useState<Machine[]>([]);
    const [loading, setLoading] = useState(true);
    const [search, setSearch] = useState("");
    const [statusFilter, setStatusFilter] = useState<OperationalAlertFilter>("all");
    const [alerts, setAlerts] = useState<OperationalAlertItem[]>([]);
    const [loadError, setLoadError] = useState(false);

    const fetchAlertsData = useCallback(async () => {
        setLoading(true);
        setLoadError(false);
        try {
            const requestedStatus = statusFilter === "resolved" ? "Resolved" : statusFilter === "all" ? "All" : "Active";
            const [machinesData, alertData] = await Promise.all([getMachines(), getOperationalAlerts(requestedStatus)]);
            setMachines(machinesData);
            setAlerts(alertData);
        } catch (error) {
            console.error("Erro ao buscar dados de alertas:", error);
            setLoadError(true);
            toast.error("Falha ao carregar a lista de alertas.");
        } finally {
            setLoading(false);
        }
    }, [statusFilter]);

    useEffect(() => {
        void fetchAlertsData();
    }, [fetchAlertsData]);

    const handleMarkRead = async (alert: OperationalAlertItem) => {
        try {
            await markOperationalAlertRead(alert.id);
            setAlerts(current => current.map(item => item.id === alert.id ? { ...item, isRead: true } : item));
        } catch (error) {
            toast.error(error instanceof Error ? error.message : "Não foi possível atualizar o alerta.");
        }
    };

    // Filter alerts based on search and status selection
    const filteredAlerts = useMemo(() => {
        return filterOperationalAlerts(alerts, search, statusFilter);
    }, [alerts, search, statusFilter]);
    const activeAlerts = useMemo(() => alerts.filter(alert => alert.status === "Active"), [alerts]);

    return (
        <SidebarProvider>
            <div className="min-h-screen flex w-full">
                <AppSidebar />
                <div className="flex-1 flex flex-col min-w-0 bg-background">
                    
                    {/* Header */}
                    <header className="h-12 flex items-center border-b bg-card px-4 gap-3">
                        <SidebarTrigger />
                        <div className="flex items-center gap-2">
                            <Bell className="w-4 h-4 text-destructive animate-pulse" />
                            <h1 className="text-sm font-semibold text-foreground">Alertas e Notificações</h1>
                        </div>
                    </header>

                    {/* Main Content */}
                    <main className="flex-1 p-6 flex flex-col gap-6 overflow-auto">
                        
                        {/* Summary Cards */}
                        <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
                            <div className="bg-card border p-4 rounded-lg flex items-center gap-4 shadow-xs">
                                <div className="h-10 w-10 rounded-full bg-red-100 dark:bg-red-950/30 flex items-center justify-center text-red-600">
                                    <AlertCircle className="w-5 h-5" />
                                </div>
                                <div>
                                    <p className="text-xs text-muted-foreground font-medium uppercase tracking-wider">Máquinas Offline</p>
                                    <p className="text-2xl font-bold font-mono-data text-foreground mt-0.5">
                                        {activeAlerts.filter(a => a.type === "TelemetryOffline").length}
                                    </p>
                                </div>
                            </div>
                            <div className="bg-card border p-4 rounded-lg flex items-center gap-4 shadow-xs">
                                <div className="h-10 w-10 rounded-full bg-amber-100 dark:bg-amber-950/30 flex items-center justify-center text-amber-600">
                                    <AlertTriangle className="w-5 h-5" />
                                </div>
                                <div>
                                    <p className="text-xs text-muted-foreground font-medium uppercase tracking-wider">Outros alertas ativos</p>
                                    <p className="text-2xl font-bold font-mono-data text-foreground mt-0.5">
                                        {activeAlerts.filter(a => a.type !== "TelemetryOffline").length}
                                    </p>
                                </div>
                            </div>
                            <div className="bg-card border p-4 rounded-lg flex items-center gap-4 shadow-xs">
                                <div className="h-10 w-10 rounded-full bg-blue-100 dark:bg-blue-950/30 flex items-center justify-center text-blue-600">
                                    <Package className="w-5 h-5" />
                                </div>
                                <div>
                                    <p className="text-xs text-muted-foreground font-medium uppercase tracking-wider">Máquinas com estoque baixo</p>
                                    <p className="text-2xl font-bold font-mono-data text-foreground mt-0.5">
                                        {machines.filter(machine => machine.lowStock).length}
                                    </p>
                                </div>
                            </div>
                        </div>

                        {/* Filters & Control bar */}
                        <div className="bg-card border rounded-lg p-4 flex flex-col md:flex-row md:items-center justify-between gap-4 shadow-xs">
                            <div className="flex flex-col sm:flex-row gap-4 items-stretch sm:items-center flex-1 max-w-3xl">
                                <div className="relative flex-1">
                                    <Search className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-muted-foreground" />
                                    <Input 
                                        type="text"
                                        placeholder="Buscar por máquina, localização ou motivo..."
                                        className="pl-9 w-full"
                                        value={search}
                                        onChange={(e) => setSearch(e.target.value)}
                                    />
                                </div>
                                <div className="w-full sm:w-48">
                                    <Select value={statusFilter} onValueChange={value => setStatusFilter(value as OperationalAlertFilter)}>
                                        <SelectTrigger>
                                            <SelectValue placeholder="Filtrar por Status" />
                                        </SelectTrigger>
                                        <SelectContent>
                                            <SelectItem value="all">Todos os Status</SelectItem>
                                            <SelectItem value="offline">Apenas Offline</SelectItem>
                                            <SelectItem value="warning">Apenas Atenção</SelectItem>
                                            <SelectItem value="resolved">Histórico resolvido</SelectItem>
                                        </SelectContent>
                                    </Select>
                                </div>
                            </div>

                            <div className="flex gap-2">
                                <Button 
                                    variant="outline"
                                    onClick={() => void fetchAlertsData()}
                                    disabled={loading}
                                    className="gap-2 text-muted-foreground hover:text-foreground"
                                    title="Sincronizar Alertas"
                                >
                                    <RefreshCw className={`w-4 h-4 ${loading ? "animate-spin" : ""}`} />
                                    Atualizar
                                </Button>
                            </div>
                        </div>

                        {/* Alerts Notification List */}
                        {loadError && alerts.length > 0 && (
                            <div role="alert" className="flex flex-wrap items-center justify-between gap-3 rounded-md border border-destructive/30 bg-destructive/5 p-3 text-sm">
                                <span>Não foi possível atualizar os alertas. A lista exibida pode estar desatualizada.</span>
                                <Button variant="outline" size="sm" onClick={() => void fetchAlertsData()} disabled={loading}>
                                    <RefreshCw className={`mr-2 h-4 w-4 ${loading ? "animate-spin" : ""}`} />
                                    Tentar novamente
                                </Button>
                            </div>
                        )}
                        {loading && alerts.length === 0 ? (
                            <div className="flex items-center justify-center py-20 bg-card border rounded-lg">
                                <p className="text-muted-foreground animate-pulse font-medium">Carregando alertas...</p>
                            </div>
                        ) : loadError && alerts.length === 0 ? (
                            <div role="alert" className="flex flex-col items-center justify-center gap-3 py-20 bg-card border rounded-lg border-dashed text-center">
                                <h3 className="text-base font-semibold text-foreground">Não foi possível carregar os alertas</h3>
                                <p className="text-sm text-muted-foreground">Confira sua conexão e tente novamente.</p>
                                <Button variant="outline" onClick={() => void fetchAlertsData()} disabled={loading}>
                                    <RefreshCw className={`mr-2 h-4 w-4 ${loading ? "animate-spin" : ""}`} />
                                    Tentar novamente
                                </Button>
                            </div>
                        ) : filteredAlerts.length === 0 ? (
                            <div className="flex flex-col items-center justify-center py-20 bg-card border rounded-lg border-dashed">
                                <Bell className="w-12 h-12 text-muted-foreground/30 mb-4" />
                                <h3 className="text-base font-semibold text-foreground">Sem alertas para exibir</h3>
                                <p className="text-sm text-muted-foreground mt-1 text-center max-w-xs">
                                    Não foram encontrados alertas correspondentes aos filtros selecionados.
                                </p>
                            </div>
                        ) : (
                            <div className="flex flex-col gap-4 animate-slide-up">
                                {filteredAlerts.map((alert) => (
                                    <div 
                                        key={alert.id}
                                        className="p-5 bg-card border rounded-lg flex flex-col sm:flex-row gap-4 justify-between items-start shadow-xs"
                                    >
                                        <div className="flex gap-4 items-start flex-1">
                                            <div className="mt-1">
                                                {alert.type === "TelemetryOffline" || alert.severity === "critical" ? (
                                                    <div className="p-2 rounded-full bg-red-100 dark:bg-red-950/20 text-red-600">
                                                        <AlertCircle className="w-4 h-4" />
                                                    </div>
                                                ) : (
                                                    <div className="p-2 rounded-full bg-amber-100 dark:bg-amber-950/20 text-amber-600">
                                                        <AlertTriangle className="w-4 h-4" />
                                                    </div>
                                                )}
                                            </div>
                                            
                                            <div className="space-y-1">
                                                <div className="flex flex-wrap items-center gap-2">
                                                    <span className="text-sm font-bold text-foreground">
                                                        {alert.title}{alert.machineId ? ` — ${alert.machineName || alert.machineId}` : ""}
                                                    </span>
                                                    <span className="text-xs bg-muted px-2 py-0.5 rounded text-muted-foreground flex items-center gap-1">
                                                        <Users className="w-3 h-3" />
                                                        {alert.clientName || "Operação"}
                                                    </span>
                                                </div>
                                                <p className="text-sm text-muted-foreground leading-relaxed">
                                                    {alert.description}
                                                    {alert.resolution && <span className="block mt-1">Resolução: {alert.resolution}</span>}
                                                </p>
                                                <div className="flex items-center gap-1.5 text-xs text-muted-foreground/75 pt-1">
                                                    <Calendar className="w-3.5 h-3.5" />
                                                    <span>{new Date(alert.occurredAt).toLocaleString("pt-BR")}</span>
                                                </div>
                                            </div>
                                        </div>

                                        <div className="sm:self-center flex items-center gap-2">
                                            <StatusIndicator status={alert.type === "TelemetryOffline" ? "offline" : "warning"} />
                                            {!alert.isRead && alert.status === "Active" && <Button variant="outline" size="sm" onClick={() => void handleMarkRead(alert)}>Marcar como lido</Button>}
                                        </div>
                                    </div>
                                ))}
                            </div>
                        )}
                    </main>
                </div>
            </div>
        </SidebarProvider>
    );
}
