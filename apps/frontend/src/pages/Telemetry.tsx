import { SidebarProvider, SidebarTrigger } from "@/components/ui/sidebar";
import { AppSidebar } from "@/components/AppSidebar";
import { useState, useEffect, useRef } from "react";
import { Button } from "@/components/ui/button";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Terminal, Send, Trash2, DoorOpen, DoorClosed, CheckCircle2, XCircle, FileText, Settings, RefreshCw, Radio, WifiOff } from "lucide-react";
import { type Machine } from "@/data/mockData";
import { getMachines, API_BASE_URL } from "@/lib/api";

type DeviceStatus = "idle" | "listening" | "connected" | "disconnected";

interface DetectedDevice {
    serial: string;
    status: DeviceStatus;
}

export default function Telemetry() {
    const [logs, setLogs] = useState<string[]>([]);
    const [selectedEsp, setSelectedEsp] = useState<string>("");
    const [ws, setWs] = useState<WebSocket | null>(null);
    const [isConnected, setIsConnected] = useState(false);
    const logEndRef = useRef<HTMLDivElement>(null);
    const [machinesList, setMachinesList] = useState<Machine[]>([]);

    // Modo de escuta automática de máquinas IoT
    const [listeningMode, setListeningMode] = useState(false);
    const [detectedDevice, setDetectedDevice] = useState<DetectedDevice | null>(null);
    const wsRef = useRef<WebSocket | null>(null);
    const listeningRef = useRef(false);

    useEffect(() => {
        getMachines().then(data => {
            setMachinesList(data);
        }).catch(err => {
            console.error("Erro ao buscar máquinas para telemetria:", err);
        });
    }, []);

    useEffect(() => {
        // Automatically scroll to bottom when new logs arrive
        logEndRef.current?.scrollIntoView({ behavior: "smooth" });
    }, [logs]);

    useEffect(() => {
        const wsUrl = API_BASE_URL
            .replace(/^http:/, "ws:")
            .replace(/^https:/, "wss:")
            .replace(/\/api$/, "/sitehandler.ashx");
        const socket = new WebSocket(wsUrl);

        socket.onopen = () => {
            setLogs(prev => [...prev, "[SISTEMA] Conectado ao WebSocket de Telemetria!"]);
            setIsConnected(true);
            setWs(socket);
            wsRef.current = socket;
        };

        socket.onmessage = (event) => {
            // Tenta tratar eventos de dispositivo IoT antes de logar
            try {
                const parsed = JSON.parse(event.data);

                if (parsed.type === "device_connected" && parsed.serial) {
                    const serial: string = parsed.serial;
                    setDetectedDevice({ serial, status: "connected" });
                    setSelectedEsp(serial);
                    setListeningMode(false);
                    listeningRef.current = false;
                    setLogs(prev => [...prev, `[IOT] Máquina identificada: ${serial} — Conectada! Recebendo telemetria...`]);
                    return;
                }

                if (parsed.type === "device_disconnected" && parsed.serial) {
                    const serial: string = parsed.serial;
                    setDetectedDevice(prev => prev?.serial === serial ? { serial, status: "disconnected" } : prev);
                    setLogs(prev => [...prev, `[IOT] Máquina ${serial} — Desconectada`]);
                    return;
                }

                // Telemetria enviada pela máquina IoT ao backend (resposta a comandos ou dados espontâneos)
                if (parsed.type === "telemetry" && parsed.serial !== undefined) {
                    const serial: string = parsed.serial;
                    const data: string = parsed.data ?? "";
                    setLogs(prev => [...prev, `[${serial}] ${data}`]);
                    return;
                }

                if (parsed.type === "ack") {
                    // Filtrar acks de listen/subscribe para não poluir o log
                    const data: string = parsed.data ?? "";
                    if (data === "listen_ok" || data === "listen_cancelled") return;
                    setLogs(prev => [...prev, `[RECEBIDO] ${event.data}`]);
                    return;
                }
            } catch {
                // não é JSON ou não é evento de dispositivo
            }

            setLogs(prev => [...prev, `[RECEBIDO] ${event.data}`]);
        };


        socket.onclose = () => {
            setLogs(prev => [...prev, "[SISTEMA] Conexão encerrada com o servidor."]);
            setIsConnected(false);
            setWs(null);
            wsRef.current = null;
        };

        socket.onerror = (err) => {
            console.error("Erro WebSocket:", err);
            setLogs(prev => [...prev, "[ERRO] Falha na conexão com o servidor WebSocket."]);
        };

        return () => {
            socket.close();
        };
    }, []);

    const handleEspChange = (val: string) => {
        setSelectedEsp(val);
        if (ws && ws.readyState === WebSocket.OPEN) {
            const subscribePayload = {
                type: "subscribe",
                target: val
            };
            ws.send(JSON.stringify(subscribePayload));
            setLogs(prev => [...prev, `[ENVIADO] ${JSON.stringify(subscribePayload)}`]);
        }
    };

    const toggleListeningMode = () => {
        if (listeningMode) {
            // Cancelar modo escuta
            setListeningMode(false);
            listeningRef.current = false;
            // Notifica backend para não auto-registrar mais
            if (wsRef.current && wsRef.current.readyState === WebSocket.OPEN) {
                wsRef.current.send(JSON.stringify({ type: "listen", cancel: true }));
            }
            setLogs(prev => [...prev, "[SISTEMA] Modo de escuta cancelado."]);
        } else {
            setListeningMode(true);
            listeningRef.current = true;
            setDetectedDevice(null);
            // Notifica backend para registrar este cliente quando o próximo ESP conectar
            if (wsRef.current && wsRef.current.readyState === WebSocket.OPEN) {
                wsRef.current.send(JSON.stringify({ type: "listen" }));
                setLogs(prev => [...prev, "[SISTEMA] Modo de escuta ativado — aguardando conexão de máquina IoT..."]);
            } else {
                setLogs(prev => [...prev, "[ERRO] WebSocket não está conectado."]);
                setListeningMode(false);
                listeningRef.current = false;
            }
        }
    };

    const enviarMsg = (command: string) => {
        if (!selectedEsp) {
            alert("Selecione um ESP válido (Serial Number).");
            return;
        }

        if (!ws || ws.readyState !== WebSocket.OPEN) {
            alert("Conexão WebSocket não está aberta.");
            return;
        }

        const payload = {
            type: "msg",
            target: selectedEsp,
            data: command
        };

        ws.send(JSON.stringify(payload));
        setLogs(prev => [...prev, `[ENVIADO] ${JSON.stringify(payload)}`]);
    };

    const limparLog = () => setLogs([]);

    // Helpers de UI para o badge do dispositivo detectado
    const deviceBadgeContent = () => {
        if (!detectedDevice) {
            if (listeningMode) return {
                label: "Aguardando conexão IoT...",
                color: "text-yellow-400",
                bg: "bg-yellow-500/10 border-yellow-500/30",
                dotClass: "bg-yellow-400",
                ping: true,
                icon: <Radio className="w-3.5 h-3.5" />,
                suffix: null
            };
            return null;
        }
        if (detectedDevice.status === "connected") return {
            label: detectedDevice.serial,
            color: "text-green-400",
            bg: "bg-green-500/10 border-green-500/30",
            dotClass: "bg-green-400",
            ping: false,
            icon: <CheckCircle2 className="w-3.5 h-3.5" />,
            suffix: "Telemetria ativa"
        };
        return {
            label: detectedDevice.serial,
            color: "text-red-400",
            bg: "bg-red-500/10 border-red-500/30",
            dotClass: "bg-red-400",
            ping: false,
            icon: <WifiOff className="w-3.5 h-3.5" />,
            suffix: "Máquina desconectada"
        };
    };

    const badge = deviceBadgeContent();

    return (
        <SidebarProvider>
            <div className="min-h-screen flex w-full">
                <AppSidebar />
                <div className="flex-1 flex flex-col min-w-0 bg-background">
                    <header className="h-12 flex items-center border-b bg-card px-4 gap-3">
                        <SidebarTrigger />
                        <div className="flex items-center gap-2">
                            <Terminal className="w-4 h-4 text-primary" />
                            <h1 className="text-sm font-semibold text-foreground">Telemetria (MDB Remoto)</h1>
                        </div>
                    </header>
                    <main className="flex-1 p-6 flex flex-col gap-6 h-[calc(100vh-3rem)]">

                        {/* CONTROLS HEADER */}
                        <div className="flex flex-col gap-4 bg-card p-4 rounded-lg border">
                            {/* Linha 1: Seletor + status servidor + botão escuta */}
                            <div className="flex flex-col md:flex-row md:items-end justify-between gap-4">
                                <div className="w-full md:w-80">
                                    <label className="text-sm font-medium mb-1.5 block text-muted-foreground">
                                        Lista de Máquinas
                                    </label>
                                    <Select value={selectedEsp} onValueChange={handleEspChange}>
                                        <SelectTrigger>
                                            <SelectValue placeholder="Selecione uma máquina..." />
                                        </SelectTrigger>
                                        <SelectContent>
                                            {machinesList.filter(m => m.id && m.id.trim() !== "" && m.serialNumber && m.serialNumber.trim() !== "").map(machine => (
                                                <SelectItem key={machine.id} value={machine.serialNumber}>{machine.name} ({machine.serialNumber})</SelectItem>
                                            ))}
                                        </SelectContent>
                                    </Select>
                                </div>
                                <div className="flex items-center gap-3 flex-wrap">
                                    {/* Status do servidor WebSocket */}
                                    <div className="flex items-center gap-2 text-sm">
                                        <div className={`w-3 h-3 rounded-full ${isConnected ? "bg-green-500 animate-pulse" : "bg-red-500"}`} />
                                        <span className="text-muted-foreground font-medium">
                                            {isConnected ? "Servidor Conectado" : "Desconectado"}
                                        </span>
                                    </div>

                                    {/* Botão modo escuta IoT */}
                                    <Button
                                        onClick={toggleListeningMode}
                                        disabled={!isConnected}
                                        variant={listeningMode ? "destructive" : "outline"}
                                        className={`gap-2 transition-all ${listeningMode ? "shadow-md shadow-red-500/20" : "border-primary/40 text-primary hover:bg-primary/10"}`}
                                    >
                                        <Radio className={`w-4 h-4 ${listeningMode ? "animate-pulse" : ""}`} />
                                        {listeningMode ? "Cancelar Escuta" : "Aguardar Máquina IoT"}
                                    </Button>
                                </div>
                            </div>

                            {/* Linha 2: Badge do dispositivo detectado (só aparece se houver estado) */}
                            {badge && (
                                <div className={`flex items-center gap-2.5 rounded-md border px-3 py-2 text-sm font-mono transition-all ${badge.bg}`}>
                                    <div className="relative flex items-center justify-center w-3 h-3">
                                        {badge.ping && (
                                            <span className={`absolute inline-flex w-full h-full rounded-full opacity-75 animate-ping ${badge.dotClass}`}></span>
                                        )}
                                        <span className={`relative inline-flex rounded-full w-2 h-2 ${badge.dotClass}`}></span>
                                    </div>
                                    <span className={`flex items-center gap-1.5 ${badge.color}`}>
                                        {badge.icon}
                                        <span className="font-semibold">{badge.label}</span>
                                    </span>
                                    {badge.suffix && (
                                        <span className="ml-auto text-xs text-muted-foreground">{badge.suffix}</span>
                                    )}
                                </div>
                            )}
                        </div>

                        {/* TERMINAL LOG */}
                        <div className="flex-1 bg-zinc-950 text-zinc-300 font-mono text-sm p-4 rounded-lg overflow-y-auto border shadow-inner flex flex-col">
                            {logs.length === 0 ? (
                                <div className="text-zinc-600 italic">Aguardando tráfego de dados...</div>
                            ) : (
                                logs.map((log, i) => (
                                    <div key={i} className={`mb-1 break-all ${
                                        log.startsWith("[ENVIADO]") ? "text-blue-400" :
                                        log.startsWith("[SISTEMA]") ? "text-yellow-400" :
                                        log.startsWith("[ERRO]") ? "text-red-400" :
                                        log.startsWith("[IOT]") ? "text-cyan-400 font-semibold" :
                                        "text-green-400"
                                    }`}>
                                        <span className="opacity-50 text-xs mr-2">[{new Date().toLocaleTimeString()}]</span>
                                        {log}
                                    </div>
                                ))
                            )}
                            <div ref={logEndRef} />
                        </div>

                        {/* ACTIONS */}
                        <div className="bg-card p-4 rounded-lg border">
                            <h3 className="text-sm font-semibold mb-3 flex items-center gap-2">
                                <Send className="w-4 h-4" /> Enviar Comandos MDB
                            </h3>
                            <div className="flex flex-wrap gap-3">
                                <Button onClick={() => enviarMsg("ABRIR_SESSAO")} variant="default" className="bg-blue-600 hover:bg-blue-700">
                                    <DoorOpen className="w-4 h-4 mr-2" /> Abrir Sessão
                                </Button>
                                <Button onClick={() => enviarMsg("FECHAR_SESSAO")} variant="destructive">
                                    <DoorClosed className="w-4 h-4 mr-2" /> Fechar Sessão
                                </Button>
                                <Button onClick={() => enviarMsg("VENDA_APROVADA")} variant="outline" className="text-green-600 border-green-600 hover:bg-green-50">
                                    <CheckCircle2 className="w-4 h-4 mr-2" /> Venda Aprovada
                                </Button>
                                <Button onClick={() => enviarMsg("VENDA_NEGADA")} variant="outline" className="text-red-600 border-red-600 hover:bg-red-50">
                                    <XCircle className="w-4 h-4 mr-2" /> Venda Negada
                                </Button>
                                <Button onClick={() => enviarMsg("RELATORIO_AUDITORIA")} variant="secondary">
                                    <FileText className="w-4 h-4 mr-2" /> Relatório
                                </Button>
                                <Button onClick={() => enviarMsg("REQUISICAO_CONFIGURACAO")} variant="secondary" className="bg-purple-100 text-purple-700 hover:bg-purple-200">
                                    <Settings className="w-4 h-4 mr-2" /> Requisição Config
                                </Button>
                                <Button onClick={() => enviarMsg("ATUALIZAR_CONFIGURACAO")} variant="secondary" className="bg-teal-100 text-teal-700 hover:bg-teal-200">
                                    <RefreshCw className="w-4 h-4 mr-2" /> Atualizar Config
                                </Button>

                                <div className="flex-1" />

                                <Button onClick={limparLog} variant="ghost" className="text-muted-foreground hover:text-red-600">
                                    <Trash2 className="w-4 h-4 mr-2" /> Limpar Log
                                </Button>
                            </div>
                        </div>

                    </main>
                </div>
            </div>
        </SidebarProvider>
    );
}
