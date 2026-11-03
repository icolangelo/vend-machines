import { SidebarProvider, SidebarTrigger } from "@/components/ui/sidebar";
import { AppSidebar } from "@/components/AppSidebar";
import { useState, useEffect, useCallback, useRef } from "react";
import { Input } from "@/components/ui/input";
import { Button } from "@/components/ui/button";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Bell, Search, AlertTriangle, AlertCircle, Calendar, Users, RefreshCw, Package } from "lucide-react";
import { type Machine } from "@/data/mockData";
import { getMachines, getOperationalAlerts, getOperationalAlertSummary, markOperationalAlertRead, resolveIncompleteSessionClosure, type OperationalAlertItem } from "@/lib/api";
import type { OperationalAlertFilter } from "@/lib/operationalAlertFilters";
import { StatusIndicator } from "@/components/dashboard/StatusIndicator";
import { toast } from "sonner";
import { useNavigate } from "react-router-dom";
import { useAuth } from "@/contexts/useAuth";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Checkbox } from "@/components/ui/checkbox";
import { Label } from "@/components/ui/label";

const ALERTS_PAGE_SIZE = 50;

export default function Alerts() {
    const navigate = useNavigate();
    const { user } = useAuth();
    const [machines, setMachines] = useState<Machine[]>([]);
    const [loading, setLoading] = useState(true);
    const [hasLoadedMachines, setHasLoadedMachines] = useState(false);
    const [hasLoadedAlerts, setHasLoadedAlerts] = useState(false);
    const [hasLoadedSummary, setHasLoadedSummary] = useState(false);
    const [search, setSearch] = useState("");
    const [statusFilter, setStatusFilter] = useState<OperationalAlertFilter>("all");
    const [page, setPage] = useState(1);
    const [loadedPage, setLoadedPage] = useState(1);
    const [totalAlerts, setTotalAlerts] = useState(0);
    const [alerts, setAlerts] = useState<OperationalAlertItem[]>([]);
    const [closureAlert, setClosureAlert] = useState<OperationalAlertItem | null>(null);
    const [closureEvidence, setClosureEvidence] = useState("");
    const [closureConfirmed, setClosureConfirmed] = useState(false);
    const [resolvingClosure, setResolvingClosure] = useState(false);
    const [alertSummary, setAlertSummary] = useState<{ disconnectedMachines: number; otherActiveAlerts: number } | null>(null);
    const [loadError, setLoadError] = useState(false);
    const [alertsLoadError, setAlertsLoadError] = useState(false);
    const requestVersion = useRef(0);

    const fetchAlertsData = useCallback(async () => {
        const currentRequest = ++requestVersion.current;
        setLoading(true);
        setLoadError(false);
        setAlertsLoadError(false);
        const requestedStatus = statusFilter === "resolved" ? "Resolved" : statusFilter === "all" ? "All" : "Active";
        const requestedType = statusFilter === "offline" ? "TelemetryOffline" : statusFilter === "warning" ? "OtherActive" : "All";
        const results = await Promise.allSettled([
            getMachines(),
            getOperationalAlerts(requestedStatus, page, ALERTS_PAGE_SIZE, requestedType, search),
            getOperationalAlertSummary(),
        ]);
        if (currentRequest !== requestVersion.current) return;

        let hasFailure = false;
        const [machinesResult, alertsResult, summaryResult] = results;
        if (machinesResult.status === "fulfilled") {
            setMachines(machinesResult.value);
            setHasLoadedMachines(true);
        } else {
            hasFailure = true;
            console.error("Erro ao buscar máquinas para os alertas:", machinesResult.reason);
        }
        if (alertsResult.status === "fulfilled") {
            setAlerts(alertsResult.value.items);
            setLoadedPage(alertsResult.value.page);
            setTotalAlerts(alertsResult.value.total);
            setHasLoadedAlerts(true);
        } else {
            hasFailure = true;
            setAlertsLoadError(true);
            console.error("Erro ao buscar a lista de alertas:", alertsResult.reason);
        }
        if (summaryResult.status === "fulfilled") {
            setAlertSummary(summaryResult.value);
            setHasLoadedSummary(true);
        } else {
            hasFailure = true;
            console.error("Erro ao buscar o resumo de alertas:", summaryResult.reason);
        }
        setLoadError(hasFailure);
        if (hasFailure) toast.error("Alguns dados de alertas não puderam ser atualizados.");
        setLoading(false);
    }, [page, search, statusFilter]);

    useEffect(() => {
        void fetchAlertsData();
        return () => { requestVersion.current += 1; };
    }, [fetchAlertsData]);

    const handleMarkRead = async (alert: OperationalAlertItem) => {
        try {
            await markOperationalAlertRead(alert.id);
            setAlerts(current => current.map(item => item.id === alert.id ? { ...item, isRead: true } : item));
        } catch (error) {
            toast.error(error instanceof Error ? error.message : "Não foi possível atualizar o alerta.");
        }
    };

    const handleResolveIncompleteClosure = async () => {
        if (!closureAlert?.sessionId) return;
        setResolvingClosure(true);
        try {
            await resolveIncompleteSessionClosure(closureAlert.sessionId, closureEvidence.trim());
            toast.success("Encerramento confirmado e registrado na auditoria.");
            setClosureAlert(null);
            setClosureEvidence("");
            setClosureConfirmed(false);
            await fetchAlertsData();
        } catch (error) {
            toast.error(error instanceof Error ? error.message : "Não foi possível confirmar o encerramento da sessão.");
        } finally {
            setResolvingClosure(false);
        }
    };

    const filteredAlerts = alerts;
    const summaryValue = (value: number | undefined, isLoaded: boolean) => isLoaded
        ? (value ?? 0).toLocaleString("pt-BR")
        : loading ? "…" : "Indisponível";
    const hasLoadedAnyData = hasLoadedMachines || hasLoadedAlerts || hasLoadedSummary;

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
                                    <p className="text-xs text-muted-foreground font-medium uppercase tracking-wider">Máquinas desconectadas</p>
                                    <p className="text-2xl font-bold font-mono-data text-foreground mt-0.5">
                                        {summaryValue(alertSummary?.disconnectedMachines, hasLoadedSummary)}
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
                                        {summaryValue(alertSummary?.otherActiveAlerts, hasLoadedSummary)}
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
                                        {summaryValue(machines.filter(machine => machine.lowStock).length, hasLoadedMachines)}
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
                                        placeholder="Buscar por máquina, cliente ou motivo..."
                                        className="pl-9 w-full"
                                        value={search}
                                        onChange={(e) => { setSearch(e.target.value); setPage(1); }}
                                    />
                                </div>
                                <div className="w-full sm:w-48">
                                    <Select value={statusFilter} onValueChange={value => { setStatusFilter(value as OperationalAlertFilter); setPage(1); }}>
                                        <SelectTrigger>
                                            <SelectValue placeholder="Filtrar por Status" />
                                        </SelectTrigger>
                                        <SelectContent>
                                            <SelectItem value="all">Todos os Status</SelectItem>
                                            <SelectItem value="offline">Apenas desconectadas</SelectItem>
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
                        {loadError && hasLoadedAnyData && (
                            <div role="alert" className="flex flex-wrap items-center justify-between gap-3 rounded-md border border-destructive/30 bg-destructive/5 p-3 text-sm">
                                <span>Alguns dados de alertas não puderam ser atualizados. As informações exibidas podem estar desatualizadas ou indisponíveis.</span>
                                <Button variant="outline" size="sm" onClick={() => void fetchAlertsData()} disabled={loading}>
                                    <RefreshCw className={`mr-2 h-4 w-4 ${loading ? "animate-spin" : ""}`} />
                                    Tentar novamente
                                </Button>
                            </div>
                        )}
                        {loading && !hasLoadedAlerts ? (
                            <div className="flex items-center justify-center py-20 bg-card border rounded-lg">
                                <p className="text-muted-foreground animate-pulse font-medium">Carregando alertas...</p>
                            </div>
                        ) : alertsLoadError && !hasLoadedAlerts ? (
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
                                            {alert.status === "Resolved" ? (
                                                <span className="text-xs rounded-full bg-muted px-2 py-1 text-muted-foreground">Resolvido</span>
                                            ) : (
                                                <StatusIndicator status={alert.type === "TelemetryOffline" ? "offline" : alert.type === "TelemetryUnmonitored" ? "unmonitored" : "warning"} />
                                            )}
                                            {alert.type === "LowStock" && alert.machineId && (
                                                <Button
                                                    variant="outline"
                                                    size="sm"
                                                    onClick={() => navigate(`/inventory?machineId=${encodeURIComponent(alert.machineId!)}`)}
                                                >
                                                    Ver estoque
                                                </Button>
                                            )}
                                            {alert.type === "InventoryMappingMismatch" && alert.machineId && (
                                                <Button
                                                    variant="outline"
                                                    size="sm"
                                                    onClick={() => navigate(`/inventory?machineId=${encodeURIComponent(alert.machineId!)}`)}
                                                >
                                                    Conferir estoque
                                                </Button>
                                            )}
                                            {["TelemetryOffline", "TelemetryWarning", "TelemetryUnmonitored", "TelemetryOwnerUnknown", "SessionFailure"].includes(alert.type) && alert.machineId && (
                                                <Button
                                                    variant="outline"
                                                    size="sm"
                                                    onClick={() => navigate(`/telemetry?machineId=${encodeURIComponent(alert.machineId!)}`)}
                                                >
                                                    Ver telemetria
                                                </Button>
                                            )}
                                            {alert.type === "PaymentPending" && alert.transactionId && (
                                                <Button variant="outline" size="sm" onClick={() => navigate(`/payments?transactionId=${encodeURIComponent(alert.transactionId!)}`)}>
                                                    Ver pagamento
                                                </Button>
                                            )}
                                            {alert.type === "PaymentPending" && user?.role === "PlatformAdmin" && (
                                                <Button variant="outline" size="sm" onClick={() => navigate("/admin?tab=payments")}>
                                                    Ver conciliação
                                                </Button>
                                            )}
                                            {alert.type === "SessionFailure" && alert.status === "Active" && alert.sessionState === "ClosureIncomplete" && alert.sessionId && user?.role !== "Operator" && (
                                                <Button variant="outline" size="sm" onClick={() => setClosureAlert(alert)}>
                                                    Confirmar máquina ociosa
                                                </Button>
                                            )}
                                            {!alert.isRead && alert.status === "Active" && <Button variant="outline" size="sm" onClick={() => void handleMarkRead(alert)}>Marcar como lido</Button>}
                                        </div>
                                    </div>
                                ))}
                            </div>
                        )}
                        {hasLoadedAlerts && totalAlerts > 0 && (
                            <div className="flex flex-col gap-3 border-t pt-4 sm:flex-row sm:items-center sm:justify-between" aria-label="Paginação de alertas">
                                <p className="text-sm text-muted-foreground">
                                    Exibindo {(loadedPage - 1) * ALERTS_PAGE_SIZE + 1}–{Math.min(loadedPage * ALERTS_PAGE_SIZE, totalAlerts)} de {totalAlerts} alertas
                                </p>
                                <div className="flex items-center gap-2">
                                    <Button variant="outline" size="sm" onClick={() => setPage(current => Math.max(1, loadedPage - 1))} disabled={loading || loadedPage <= 1}>
                                        Anterior
                                    </Button>
                                    <span className="text-sm text-muted-foreground">Página {loadedPage} de {Math.ceil(totalAlerts / ALERTS_PAGE_SIZE)}</span>
                                    <Button variant="outline" size="sm" onClick={() => setPage(loadedPage + 1)} disabled={loading || loadedPage * ALERTS_PAGE_SIZE >= totalAlerts}>
                                        Próxima
                                    </Button>
                                </div>
                            </div>
                        )}
                    </main>
                </div>
            </div>
            <Dialog open={closureAlert !== null} onOpenChange={open => {
                if (!open && !resolvingClosure) {
                    setClosureAlert(null);
                    setClosureEvidence("");
                    setClosureConfirmed(false);
                }
            }}>
                <DialogContent>
                    <DialogHeader>
                        <DialogTitle>Confirmar encerramento físico</DialogTitle>
                        <DialogDescription>
                            Inspecione a máquina antes de continuar. Só confirme se ela está ociosa e segura para receber uma nova sessão. Esta ação será registrada com seu usuário e uma referência de evidência.
                        </DialogDescription>
                    </DialogHeader>
                    <div className="space-y-4">
                        <div className="space-y-2">
                            <Label htmlFor="closure-evidence-reference">Referência do chamado ou registro</Label>
                            <Input
                                id="closure-evidence-reference"
                                value={closureEvidence}
                                onChange={event => setClosureEvidence(event.target.value)}
                                placeholder="SUP-1234"
                                maxLength={64}
                                autoComplete="off"
                            />
                        </div>
                        <div className="flex items-start gap-2">
                            <Checkbox
                                id="closure-machine-idle-confirmation"
                                checked={closureConfirmed}
                                onCheckedChange={checked => setClosureConfirmed(checked === true)}
                            />
                            <Label htmlFor="closure-machine-idle-confirmation" className="leading-5">
                                Inspecionei a máquina e confirmei que não há sessão ou entrega em andamento.
                            </Label>
                        </div>
                    </div>
                    <DialogFooter>
                        <Button variant="outline" onClick={() => setClosureAlert(null)} disabled={resolvingClosure}>Cancelar</Button>
                        <Button
                            onClick={() => void handleResolveIncompleteClosure()}
                            disabled={resolvingClosure || !closureConfirmed || !/^[A-Za-z0-9._/:_-]{3,64}$/.test(closureEvidence.trim())}
                        >
                            {resolvingClosure ? "Salvando…" : "Registrar confirmação"}
                        </Button>
                    </DialogFooter>
                </DialogContent>
            </Dialog>
        </SidebarProvider>
    );
}
