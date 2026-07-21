import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Activity, AlertTriangle, CircleDollarSign, DoorOpen, RefreshCw, Search, Send, Server, Wifi, WifiOff, XCircle } from "lucide-react";
import { AppSidebar } from "@/components/AppSidebar";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { ScrollArea } from "@/components/ui/scroll-area";
import { SidebarProvider, SidebarTrigger } from "@/components/ui/sidebar";
import { useToast } from "@/components/ui/use-toast";
import {
    API_BASE_URL,
    cancelTelemetrySession,
    createTelemetrySocketTicket,
    getTelemetryConnections,
    getTelemetryEvents,
    sendTelemetryCommand,
    setTelemetryMonitoring,
    startTelemetrySession,
    type TelemetryConnection,
    type TelemetryEventItem,
} from "@/lib/api";

const stateLabels: Record<string, string> = {
    Opening: "Abrindo sessão",
    AwaitingSelection: "Aguardando escolha do produto",
    ClosingWithoutSelection: "Encerrando sem seleção",
    DeviceClosingBeforeSelection: "Máquina encerrando a sessão",
    PaymentPending: "Aguardando pagamento Pix",
    PaymentApproved: "Pagamento aprovado",
    AwaitingDeliveryResult: "Aguardando entrega",
    DeliveryFailed: "Falha na entrega",
    RefundPending: "Estorno em processamento",
    Refunded: "Pagamento estornado",
    ReconciliationRequired: "Conciliação necessária",
    DeniedAwaitingClosure: "Venda negada; encerrando",
    Completed: "Entrega concluída",
    ClosedWithoutSelection: "Encerrada sem seleção",
    ClosedByDeviceBeforeSelection: "Encerrada pela máquina",
    ClosureIncomplete: "Encerramento incompleto",
    Denied: "Venda negada",
};

const cancellableSessionStates = new Set(["Opening", "AwaitingSelection", "PaymentPending"]);

const formatDate = (value?: string | null) => value
    ? new Date(value).toLocaleString("pt-BR")
    : "—";

export default function Telemetry() {
    const { toast } = useToast();
    const [connections, setConnections] = useState<TelemetryConnection[]>([]);
    const [events, setEvents] = useState<TelemetryEventItem[]>([]);
    const [selectedId, setSelectedId] = useState<string>("");
    const [search, setSearch] = useState("");
    const [loading, setLoading] = useState(true);
    const [busy, setBusy] = useState(false);
    const [realtimeConnected, setRealtimeConnected] = useState(false);
    const socketRef = useRef<WebSocket | null>(null);
    const reconnectTimerRef = useRef<number>();
    const selectedIdRef = useRef(selectedId);

    const loadConnections = useCallback(async () => {
        try {
            const data = await getTelemetryConnections();
            setConnections(data);
            setSelectedId(current => current || data[0]?.machineId || "");
        } catch (error) {
            toast({ title: "Erro ao carregar conexões", description: (error as Error).message, variant: "destructive" });
        } finally {
            setLoading(false);
        }
    }, [toast]);

    const loadEvents = useCallback(async (machineId: string) => {
        if (!machineId) return;
        try { setEvents(await getTelemetryEvents(machineId)); }
        catch { setEvents([]); }
    }, []);

    useEffect(() => { selectedIdRef.current = selectedId; }, [selectedId]);

    useEffect(() => {
        loadConnections();
        const timer = window.setInterval(loadConnections, 15000);
        return () => window.clearInterval(timer);
    }, [loadConnections]);

    useEffect(() => { loadEvents(selectedId); }, [selectedId, loadEvents]);

    useEffect(() => {
        let disposed = false;
        let attempts = 0;

        const connect = async () => {
            try {
                const { ticket } = await createTelemetrySocketTicket();
                if (disposed) return;
                const origin = API_BASE_URL.replace(/\/api$/, "").replace(/^http:/, "ws:").replace(/^https:/, "wss:");
                const socket = new WebSocket(`${origin}/ws/telemetry?ticket=${encodeURIComponent(ticket)}`);
                socketRef.current = socket;
                socket.onopen = () => {
                    attempts = 0;
                    setRealtimeConnected(true);
                    const machineId = selectedIdRef.current;
                    if (machineId) socket.send(JSON.stringify({ type: "subscribe", machineIds: [machineId] }));
                };
                socket.onmessage = event => {
                    try {
                        const message = JSON.parse(event.data);
                        if (message.type === "connection.updated") {
                            setConnections(current => current.map(item => item.machineId === message.machineId
                                ? { ...item, online: message.online, lastSeenAt: message.lastSeenAt ?? item.lastSeenAt }
                                : item));
                        }
                        if (message.type === "monitoring.updated") {
                            setConnections(current => current.map(item => item.machineId === message.machineId
                                ? { ...item, monitoringEnabled: message.enabled }
                                : item));
                        }
                        if (["sale.updated", "telemetry.received"].includes(message.type)) {
                            if (message.machineId === selectedIdRef.current) loadEvents(message.machineId);
                            if (message.type === "sale.updated") loadConnections();
                        }
                    } catch { /* mensagem fora do contrato */ }
                };
                socket.onclose = () => {
                    setRealtimeConnected(false);
                    socketRef.current = null;
                    if (!disposed) {
                        attempts += 1;
                        reconnectTimerRef.current = window.setTimeout(connect, Math.min(30000, 1000 * 2 ** attempts));
                    }
                };
                socket.onerror = () => socket.close();
            } catch {
                if (!disposed) reconnectTimerRef.current = window.setTimeout(connect, 5000);
            }
        };

        connect();
        return () => {
            disposed = true;
            if (reconnectTimerRef.current) window.clearTimeout(reconnectTimerRef.current);
            socketRef.current?.close();
        };
    }, [loadConnections, loadEvents]);

    useEffect(() => {
        const socket = socketRef.current;
        if (socket?.readyState === WebSocket.OPEN && selectedId) {
            socket.send(JSON.stringify({ type: "subscribe", machineIds: [selectedId] }));
        }
    }, [selectedId]);

    const selected = connections.find(item => item.machineId === selectedId);
    const canCancelSession = selected?.activeSession
        ? cancellableSessionStates.has(selected.activeSession.state)
        : false;
    const filtered = useMemo(() => {
        const term = search.trim().toLowerCase();
        return connections.filter(item => !term || item.machineName.toLowerCase().includes(term) || item.serialNumber.toLowerCase().includes(term));
    }, [connections, search]);

    const run = async (operation: () => Promise<unknown>, success: string) => {
        setBusy(true);
        try {
            await operation();
            toast({ title: success });
            await loadConnections();
            if (selectedId) await loadEvents(selectedId);
        } catch (error) {
            toast({ title: "Operação não concluída", description: (error as Error).message, variant: "destructive" });
        } finally { setBusy(false); }
    };

    return (
        <SidebarProvider>
            <div className="min-h-screen flex w-full bg-muted/20">
                <AppSidebar />
                <div className="flex-1 min-w-0">
                    <header className="h-14 flex items-center justify-between border-b bg-card px-4">
                        <div className="flex items-center gap-3">
                            <SidebarTrigger />
                            <div>
                                <h1 className="text-sm font-semibold">Conexões e sessões MDB</h1>
                                <p className="text-xs text-muted-foreground">Máquinas e vendas da sua empresa</p>
                            </div>
                        </div>
                        <Badge variant={realtimeConnected ? "default" : "destructive"} className="gap-1.5">
                            {realtimeConnected ? <Wifi className="h-3.5 w-3.5" /> : <WifiOff className="h-3.5 w-3.5" />}
                            {realtimeConnected ? "Tempo real conectado" : "Reconectando"}
                        </Badge>
                    </header>

                    <main className="p-4 lg:p-6 grid gap-4 lg:grid-cols-[360px_minmax(0,1fr)]">
                        <Card className="lg:h-[calc(100vh-6.5rem)]">
                            <CardHeader className="pb-3">
                                <CardTitle className="text-base flex items-center justify-between">
                                    Máquinas <Badge variant="secondary">{connections.length}</Badge>
                                </CardTitle>
                                <div className="relative">
                                    <Search className="absolute left-3 top-2.5 h-4 w-4 text-muted-foreground" />
                                    <Input value={search} onChange={event => setSearch(event.target.value)} placeholder="Buscar máquina ou serial" className="pl-9" />
                                </div>
                            </CardHeader>
                            <CardContent className="p-0">
                                <ScrollArea className="h-[calc(100vh-13rem)]">
                                    {loading && <p className="p-4 text-sm text-muted-foreground">Carregando conexões...</p>}
                                    {filtered.map(machine => (
                                        <button key={machine.machineId} onClick={() => setSelectedId(machine.machineId)}
                                            className={`w-full text-left p-4 border-t hover:bg-muted/60 transition-colors ${selectedId === machine.machineId ? "bg-primary/5 border-l-2 border-l-primary" : ""}`}>
                                            <div className="flex items-start justify-between gap-3">
                                                <div className="min-w-0">
                                                    <p className="font-medium text-sm truncate">{machine.machineName}</p>
                                                    <p className="text-xs text-muted-foreground font-mono">{machine.serialNumber}</p>
                                                </div>
                                                <span className={`mt-1 h-2.5 w-2.5 rounded-full ${machine.online ? "bg-emerald-500" : "bg-slate-300"}`} />
                                            </div>
                                            <div className="mt-2 flex gap-1.5 flex-wrap">
                                                <Badge variant={machine.monitoringEnabled ? "default" : "outline"}>{machine.monitoringEnabled ? "Ativa" : "Inativa"}</Badge>
                                                {machine.activeSession && <Badge variant="secondary">{stateLabels[machine.activeSession.state] ?? machine.activeSession.state}</Badge>}
                                            </div>
                                        </button>
                                    ))}
                                </ScrollArea>
                            </CardContent>
                        </Card>

                        {selected ? (
                            <div className="space-y-4 min-w-0">
                                <Card>
                                    <CardHeader className="pb-3">
                                        <div className="flex flex-col md:flex-row md:items-start justify-between gap-4">
                                            <div>
                                                <CardTitle className="text-lg">{selected.machineName}</CardTitle>
                                                <p className="text-sm text-muted-foreground">{selected.serialNumber} · {selected.location}</p>
                                            </div>
                                            <div className="flex gap-2 flex-wrap">
                                                <Button variant="outline" disabled={busy} onClick={() => run(
                                                    () => setTelemetryMonitoring(selected.machineId, !selected.monitoringEnabled),
                                                    selected.monitoringEnabled ? "Acompanhamento desativado" : "Acompanhamento ativado") }>
                                                    <Activity className="h-4 w-4 mr-2" />
                                                    {selected.monitoringEnabled ? "Desativar acompanhamento" : "Ativar acompanhamento"}
                                                </Button>
                                                {!selected.activeSession ? (
                                                    <Button disabled={busy || !selected.online || !selected.monitoringEnabled} onClick={() => run(
                                                        () => startTelemetrySession(selected.machineId), "Sessão solicitada") }>
                                                        <DoorOpen className="h-4 w-4 mr-2" /> Abrir sessão
                                                    </Button>
                                                ) : canCancelSession ? (
                                                    <Button variant="destructive" disabled={busy} onClick={() => run(
                                                        () => cancelTelemetrySession(selected.activeSession!.sessionId), "Encerramento solicitado") }>
                                                        <XCircle className="h-4 w-4 mr-2" />
                                                        {selected.activeSession.state === "Opening" || selected.activeSession.state === "AwaitingSelection"
                                                            ? "Encerrar sessão"
                                                            : "Cancelar venda"}
                                                    </Button>
                                                ) : null}
                                            </div>
                                        </div>
                                    </CardHeader>
                                    <CardContent className="grid sm:grid-cols-2 xl:grid-cols-4 gap-3">
                                        <StatusItem icon={selected.online ? Wifi : WifiOff} label="Conexão física" value={selected.online ? "Online" : "Offline"} alert={!selected.online} />
                                        <StatusItem icon={Server} label="Último contato" value={formatDate(selected.lastSeenAt)} />
                                        <StatusItem icon={Activity} label="Sessão MDB" value={selected.activeSession ? stateLabels[selected.activeSession.state] ?? selected.activeSession.state : "Sem sessão"} />
                                        <StatusItem icon={CircleDollarSign} label="Venda" value={selected.activeSession?.amountCents != null ? (selected.activeSession.amountCents / 100).toLocaleString("pt-BR", { style: "currency", currency: "BRL" }) : "—"} />
                                    </CardContent>
                                </Card>

                                {selected.activeSession && (
                                    <Card className={selected.activeSession.state === "ReconciliationRequired" ? "border-red-300" : ""}>
                                        <CardHeader><CardTitle className="text-base">Sessão em andamento</CardTitle></CardHeader>
                                        <CardContent className="grid sm:grid-cols-2 gap-3 text-sm">
                                            <Detail label="Estado" value={stateLabels[selected.activeSession.state] ?? selected.activeSession.state} />
                                            <Detail label="Iniciada em" value={formatDate(selected.activeSession.startedAt)} />
                                            <Detail label="Produto" value={selected.activeSession.itemNumber != null ? `Item ${selected.activeSession.itemNumber}` : "Aguardando seleção"} />
                                            <Detail label="Prazo atual" value={formatDate(selected.activeSession.selectionDeadlineAt ?? selected.activeSession.paymentDeadlineAt ?? selected.activeSession.deliveryDeadlineAt)} />
                                            {selected.activeSession.closeReason && <Detail label="Motivo" value={selected.activeSession.closeReason} />}
                                        </CardContent>
                                    </Card>
                                )}

                                <Card>
                                    <CardHeader className="flex flex-row items-center justify-between">
                                        <CardTitle className="text-base">Eventos em tempo real</CardTitle>
                                        <Button size="sm" variant="ghost" onClick={() => loadEvents(selected.machineId)}><RefreshCw className="h-4 w-4" /></Button>
                                    </CardHeader>
                                    <CardContent>
                                        <ScrollArea className="h-[320px] rounded-md bg-slate-950 p-3">
                                            {events.length === 0 && <p className="text-xs text-slate-500">Nenhum evento registrado.</p>}
                                            {events.map(event => (
                                                <div key={event.id} className="border-b border-slate-800 py-2 text-xs font-mono">
                                                    <div className="flex justify-between gap-3 text-slate-500">
                                                        <span>{event.eventType}</span><span>{formatDate(event.createdAt)}</span>
                                                    </div>
                                                    {event.detail && <p className="mt-1 text-slate-200">{event.detail}</p>}
                                                    {event.dataJson && <p className="mt-1 text-emerald-400 break-all">{event.dataJson}</p>}
                                                </div>
                                            ))}
                                        </ScrollArea>
                                        <div className="mt-3 flex gap-2 flex-wrap">
                                            {["RELATORIO_AUDITORIA", "REQUISICAO_CONFIGURACAO", "ATUALIZAR_CONFIGURACAO"].map(command => (
                                                <Button key={command} size="sm" variant="outline" disabled={busy || !selected.online || !selected.monitoringEnabled}
                                                    onClick={() => run(() => sendTelemetryCommand(selected.machineId, command), "Comando confirmado pela máquina") }>
                                                    <Send className="h-3.5 w-3.5 mr-2" />{command.replaceAll("_", " ")}
                                                </Button>
                                            ))}
                                        </div>
                                    </CardContent>
                                </Card>
                            </div>
                        ) : (
                            <Card className="flex items-center justify-center min-h-80"><p className="text-muted-foreground">Selecione uma máquina.</p></Card>
                        )}
                    </main>
                </div>
            </div>
        </SidebarProvider>
    );
}

function StatusItem({ icon: Icon, label, value, alert = false }: { icon: typeof Wifi; label: string; value: string; alert?: boolean }) {
    return <div className="rounded-lg border p-3 flex gap-3 items-center">
        <div className={`rounded-full p-2 ${alert ? "bg-red-50 text-red-600" : "bg-primary/10 text-primary"}`}>
            {alert ? <AlertTriangle className="h-4 w-4" /> : <Icon className="h-4 w-4" />}
        </div>
        <div className="min-w-0"><p className="text-xs text-muted-foreground">{label}</p><p className="text-sm font-medium truncate">{value}</p></div>
    </div>;
}

function Detail({ label, value }: { label: string; value: string }) {
    return <div className="rounded-md bg-muted/50 p-3"><p className="text-xs text-muted-foreground">{label}</p><p className="mt-1 font-medium">{value}</p></div>;
}
