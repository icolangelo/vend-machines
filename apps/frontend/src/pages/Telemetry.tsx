import { SidebarProvider, SidebarTrigger } from "@/components/ui/sidebar";
import { AppSidebar } from "@/components/AppSidebar";
import { useState, useEffect, useRef } from "react";
import { Button } from "@/components/ui/button";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Terminal, Send, Trash2, Power, DoorOpen, DoorClosed, CheckCircle2, XCircle, FileText, Settings, RefreshCw } from "lucide-react";
import { type Machine } from "@/data/mockData";
import { getMachines } from "@/lib/api";

export default function Telemetry() {
    const [logs, setLogs] = useState<string[]>([]);
    const [selectedEsp, setSelectedEsp] = useState<string>("");
    const [ws, setWs] = useState<WebSocket | null>(null);
    const [isConnected, setIsConnected] = useState(false);
    const logEndRef = useRef<HTMLDivElement>(null);
    const [machinesList, setMachinesList] = useState<Machine[]>([]);

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
        // Connect to the raw websocket endpoint on the .NET API
        // In dev, the API runs on port 5118
        const socket = new WebSocket("ws://localhost:5118/sitehandler.ashx");

        socket.onopen = () => {
            setLogs(prev => [...prev, "[SISTEMA] Conectado ao WebSocket de Telemetria!"]);
            setIsConnected(true);
            setWs(socket);
        };

        socket.onmessage = (event) => {
            setLogs(prev => [...prev, `[RECEBIDO] ${event.data}`]);
        };

        socket.onclose = () => {
            setLogs(prev => [...prev, "[SISTEMA] Conexão encerrada com o servidor."]);
            setIsConnected(false);
            setWs(null);
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
                        <div className="flex flex-col md:flex-row md:items-end justify-between gap-4 bg-card p-4 rounded-lg border">
                            <div className="w-full md:w-80">
                                <label className="text-sm font-medium mb-1.5 block text-muted-foreground">
                                    Lista de Máquinas
                                </label>
                                <Select value={selectedEsp} onValueChange={handleEspChange}>
                                    <SelectTrigger>
                                        <SelectValue placeholder="Selecione uma máquina..." />
                                    </SelectTrigger>
                                    <SelectContent>
                                        {machinesList.filter(m => m.serialNumber).map(machine => (
                                            <SelectItem key={machine.id} value={machine.serialNumber}>{machine.name} ({machine.serialNumber})</SelectItem>
                                        ))}
                                    </SelectContent>
                                </Select>
                            </div>
                            <div className="flex items-center gap-3">
                                <div className="flex items-center gap-2 text-sm">
                                    <div className={`w-3 h-3 rounded-full ${isConnected ? "bg-green-500 animate-pulse" : "bg-red-500"}`} />
                                    <span className="text-muted-foreground font-medium">
                                        {isConnected ? "Conectado ao Servidor" : "Desconectado"}
                                    </span>
                                </div>
                            </div>
                        </div>

                        {/* TERMINAL LOG */}
                        <div className="flex-1 bg-zinc-950 text-zinc-300 font-mono text-sm p-4 rounded-lg overflow-y-auto border shadow-inner flex flex-col">
                            {logs.length === 0 ? (
                                <div className="text-zinc-600 italic">Aguardando tráfego de dados...</div>
                            ) : (
                                logs.map((log, i) => (
                                    <div key={i} className={`mb-1 break-all ${log.startsWith("[ENVIADO]") ? "text-blue-400" : log.startsWith("[SISTEMA]") ? "text-yellow-400" : log.startsWith("[ERRO]") ? "text-red-400" : "text-green-400"}`}>
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
