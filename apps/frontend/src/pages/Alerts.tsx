import { SidebarProvider, SidebarTrigger } from "@/components/ui/sidebar";
import { AppSidebar } from "@/components/AppSidebar";
import { useState, useEffect, useMemo } from "react";
import { Input } from "@/components/ui/input";
import { Button } from "@/components/ui/button";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Bell, Search, AlertTriangle, AlertCircle, Calendar, Users, RefreshCw, CheckCheck } from "lucide-react";
import { type Machine } from "@/data/mockData";
import { getMachines } from "@/lib/api";
import { StatusIndicator } from "@/components/dashboard/StatusIndicator";
import { toast } from "sonner";

interface AlertNotification {
    id: string;
    machineId: string;
    machineName: string;
    clientName: string;
    status: "warning" | "offline";
    date: string;
    reason: string;
    isRead: boolean;
}

export default function Alerts() {
    const [machines, setMachines] = useState<Machine[]>([]);
    const [loading, setLoading] = useState(true);
    const [search, setSearch] = useState("");
    const [statusFilter, setStatusFilter] = useState<string>("all");
    const [alerts, setAlerts] = useState<AlertNotification[]>([]);

    const fetchAlertsData = async () => {
        setLoading(true);
        try {
            const machinesData = await getMachines();
            setMachines(machinesData);

            // Generate active alerts from current warning/offline machines
            const activeAlerts: AlertNotification[] = machinesData
                .filter(m => m.status === "offline" || m.status === "warning")
                .map((m, idx) => {
                    let reason = "";
                    if (m.status === "offline") {
                        reason = "Sem sinal de comunicação com o servidor há mais de 24 horas.";
                    } else if (m.stockLevel <= 20) {
                        reason = `Nível de estoque crítico: apenas ${m.stockLevel}% da capacidade preenchida.`;
                    } else {
                        reason = `Estoque abaixo do limite preventivo configurado: ${m.stockLevel}%.`;
                    }

                    // Format realistic recent date based on sync info or last hours
                    let alertDate = "Hoje, há pouco";
                    if (m.lastSync.includes("atrás")) {
                        alertDate = `Hoje, há ${m.lastSync}`;
                    } else if (m.lastSync.includes("dias")) {
                        alertDate = `Há ${m.lastSync}`;
                    }

                    return {
                        id: `alert-active-${m.id}-${idx}`,
                        machineId: m.id,
                        machineName: m.name,
                        clientName: m.clientName,
                        status: m.status as "warning" | "offline",
                        date: alertDate,
                        reason: reason,
                        isRead: false
                    };
                });

            // Mocked historical alert logs to enrich the list and show history
            const historicalAlerts: AlertNotification[] = [
                {
                    id: "alert-hist-1",
                    machineId: "VM-003",
                    machineName: "UTI C1",
                    clientName: "Hospital São Luiz",
                    status: "warning",
                    date: "Ontem às 18:45",
                    reason: "Flutuação térmica incomum no refrigerador interno (temperatura registrada: 7.2°C).",
                    isRead: true
                },
                {
                    id: "alert-hist-2",
                    machineId: "VM-004",
                    machineName: "Recepção D1",
                    clientName: "Faculdade Anhanguera",
                    status: "warning",
                    date: "Ontem às 10:12",
                    reason: "Validador de moedas MDB relatou bloqueio físico temporário. Resolvido automaticamente.",
                    isRead: true
                },
                {
                    id: "alert-hist-3",
                    machineId: "VM-011",
                    machineName: "Escritório K1",
                    clientName: "WeWork Faria Lima",
                    status: "offline",
                    date: "02 Jun 2026, 14:00",
                    reason: "Falha de autenticação de token de rede Wi-Fi. Equipamento reestabelecido após sincronia manual.",
                    isRead: true
                },
                {
                    id: "alert-hist-4",
                    machineId: "VM-008",
                    machineName: "Terminal H1",
                    clientName: "Rodoviária Tietê",
                    status: "warning",
                    date: "01 Jun 2026, 23:30",
                    reason: "Tentativa de abertura de porta sem autorização externa detectada. Alerta de segurança gerado.",
                    isRead: true
                }
            ];

            // Combine active alerts first, then historical logs
            setAlerts([...activeAlerts, ...historicalAlerts]);
        } catch (error) {
            console.error("Erro ao buscar dados de alertas:", error);
            toast.error("Falha ao carregar a lista de alertas.");
        } finally {
            setLoading(false);
        }
    };

    useEffect(() => {
        fetchAlertsData();
    }, []);

    // Mark all as read
    const handleMarkAllRead = () => {
        setAlerts(prev => prev.map(a => ({ ...a, isRead: true })));
        toast.success("Todos os alertas foram marcados como lidos.");
    };

    // Mark single alert as read/unread
    const toggleReadStatus = (id: string) => {
        setAlerts(prev => prev.map(a => a.id === id ? { ...a, isRead: !a.isRead } : a));
    };

    // Filter alerts based on search and status selection
    const filteredAlerts = useMemo(() => {
        return alerts.filter(alert => {
            const matchesSearch = 
                alert.machineName.toLowerCase().includes(search.toLowerCase()) ||
                alert.clientName.toLowerCase().includes(search.toLowerCase()) ||
                alert.machineId.toLowerCase().includes(search.toLowerCase()) ||
                alert.reason.toLowerCase().includes(search.toLowerCase());

            const matchesStatus = 
                statusFilter === "all" || 
                (statusFilter === "warning" && alert.status === "warning") ||
                (statusFilter === "offline" && alert.status === "offline");

            return matchesSearch && matchesStatus;
        });
    }, [alerts, search, statusFilter]);

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
                            <div className="bg-card border p-4 rounded-lg flex items-center gap-4 shadow-sm">
                                <div className="h-10 w-10 rounded-full bg-red-100 dark:bg-red-950/30 flex items-center justify-center text-red-600">
                                    <AlertCircle className="w-5 h-5" />
                                </div>
                                <div>
                                    <p className="text-xs text-muted-foreground font-medium uppercase tracking-wider">Máquinas Offline</p>
                                    <p className="text-2xl font-bold font-mono-data text-foreground mt-0.5">
                                        {alerts.filter(a => a.status === "offline" && !a.isRead).length}
                                    </p>
                                </div>
                            </div>
                            <div className="bg-card border p-4 rounded-lg flex items-center gap-4 shadow-sm">
                                <div className="h-10 w-10 rounded-full bg-amber-100 dark:bg-amber-950/30 flex items-center justify-center text-amber-600">
                                    <AlertTriangle className="w-5 h-5" />
                                </div>
                                <div>
                                    <p className="text-xs text-muted-foreground font-medium uppercase tracking-wider">Status de Atenção</p>
                                    <p className="text-2xl font-bold font-mono-data text-foreground mt-0.5">
                                        {alerts.filter(a => a.status === "warning" && !a.isRead).length}
                                    </p>
                                </div>
                            </div>
                            <div className="bg-card border p-4 rounded-lg flex items-center gap-4 shadow-sm">
                                <div className="h-10 w-10 rounded-full bg-blue-100 dark:bg-blue-950/30 flex items-center justify-center text-blue-600">
                                    <CheckCheck className="w-5 h-5" />
                                </div>
                                <div>
                                    <p className="text-xs text-muted-foreground font-medium uppercase tracking-wider">Histórico Lido</p>
                                    <p className="text-2xl font-bold font-mono-data text-foreground mt-0.5">
                                        {alerts.filter(a => a.isRead).length}
                                    </p>
                                </div>
                            </div>
                        </div>

                        {/* Filters & Control bar */}
                        <div className="bg-card border rounded-lg p-4 flex flex-col md:flex-row md:items-center justify-between gap-4 shadow-sm">
                            <div className="flex flex-col sm:flex-row gap-4 items-stretch sm:items-center flex-1 max-w-3xl">
                                <div className="relative flex-1">
                                    <Search className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-muted-foreground" />
                                    <Input 
                                        type="text"
                                        placeholder="Buscar por máquina, cliente ou motivo..."
                                        className="pl-9 w-full"
                                        value={search}
                                        onChange={(e) => setSearch(e.target.value)}
                                    />
                                </div>
                                <div className="w-full sm:w-48">
                                    <Select value={statusFilter} onValueChange={setStatusFilter}>
                                        <SelectTrigger>
                                            <SelectValue placeholder="Filtrar por Status" />
                                        </SelectTrigger>
                                        <SelectContent>
                                            <SelectItem value="all">Todos os Status</SelectItem>
                                            <SelectItem value="offline">Apenas Offline</SelectItem>
                                            <SelectItem value="warning">Apenas Atenção</SelectItem>
                                        </SelectContent>
                                    </Select>
                                </div>
                            </div>

                            <div className="flex gap-2">
                                <Button 
                                    variant="outline"
                                    onClick={fetchAlertsData}
                                    disabled={loading}
                                    className="gap-2 text-muted-foreground hover:text-foreground"
                                    title="Sincronizar Alertas"
                                >
                                    <RefreshCw className={`w-4 h-4 ${loading ? "animate-spin" : ""}`} />
                                    Atualizar
                                </Button>
                                <Button 
                                    variant="outline"
                                    onClick={handleMarkAllRead}
                                    className="gap-2 text-muted-foreground hover:text-foreground"
                                >
                                    <CheckCheck className="w-4 h-4 text-success" />
                                    Marcar todos como lidos
                                </Button>
                            </div>
                        </div>

                        {/* Alerts Notification List */}
                        {loading ? (
                            <div className="flex items-center justify-center py-20 bg-card border rounded-lg">
                                <p className="text-muted-foreground animate-pulse font-medium">Carregando alertas...</p>
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
                                        onClick={() => toggleReadStatus(alert.id)}
                                        className={`p-5 bg-card border rounded-lg flex flex-col sm:flex-row gap-4 justify-between items-start transition-all cursor-pointer select-none shadow-sm hover:border-muted-foreground/30 ${
                                            !alert.isRead ? "border-l-4 border-l-primary font-medium" : "opacity-75"
                                        }`}
                                    >
                                        <div className="flex gap-4 items-start flex-1">
                                            <div className="mt-1">
                                                {alert.status === "offline" ? (
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
                                                        {alert.machineName} ({alert.machineId})
                                                    </span>
                                                    <span className="text-xs bg-muted px-2 py-0.5 rounded text-muted-foreground flex items-center gap-1">
                                                        <Users className="w-3 h-3" />
                                                        {alert.clientName}
                                                    </span>
                                                    {!alert.isRead && (
                                                        <span className="text-[10px] uppercase tracking-wide bg-primary/10 text-primary px-1.5 py-0.5 rounded font-bold">
                                                            Novo
                                                        </span>
                                                    )}
                                                </div>
                                                <p className="text-sm text-muted-foreground leading-relaxed">
                                                    {alert.reason}
                                                </p>
                                                <div className="flex items-center gap-1.5 text-xs text-muted-foreground/75 pt-1">
                                                    <Calendar className="w-3.5 h-3.5" />
                                                    <span>{alert.date}</span>
                                                </div>
                                            </div>
                                        </div>

                                        <div className="sm:self-center flex items-center gap-2">
                                            <StatusIndicator status={alert.status} />
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
