import { SidebarProvider, SidebarTrigger } from "@/components/ui/sidebar";
import { AppSidebar } from "@/components/AppSidebar";
import { useState, useEffect } from "react";
import { useNavigate } from "react-router-dom";
import { getMachines, createPixQrCode, simulateWebhook, getTransactionLogs, type Machine, type PaymentTransaction, type TransactionTelemetryLog } from "@/lib/api";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { QrCode, CreditCard, Play, CheckCircle, XCircle, RefreshCw, Terminal, Copy, Check } from "lucide-react";
import { useToast } from "@/components/ui/use-toast";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";

export default function PaymentSimulator() {
    const navigate = useNavigate();
    const { toast } = useToast();
    const [machines, setMachines] = useState<Machine[]>([]);
    const [loadingMachines, setLoadingMachines] = useState(true);

    // Form states
    const [selectedMachineId, setSelectedMachineId] = useState("");
    const [amount, setAmount] = useState<string>("5.00");
    const [payerEmail, setPayerEmail] = useState("comprador@vendmachine.com.br");
    const [payerFirstName, setPayerFirstName] = useState("Cliente");
    const [payerLastName, setPayerLastName] = useState("Vending");
    const [payerCpf, setPayerCpf] = useState("");
    const [useRealMercadoPago, setUseRealMercadoPago] = useState(false);

    // Execution states
    const [generating, setGenerating] = useState(false);
    const [transaction, setTransaction] = useState<{
        transactionId: string;
        qrCode: string;
        qrCodeBase64: string;
        status: string;
        applicationFee?: number;
        applicationFeeApplied?: boolean;
        applicationFeeWarning?: string | null;
        simulated?: boolean;
    } | null>(null);
    const [copied, setCopied] = useState(false);
    const [simulating, setSimulating] = useState<"approved" | "rejected" | null>(null);

    // Logs states
    const [telemetryLogs, setTelemetryLogs] = useState<TransactionTelemetryLog[]>([]);
    const [pollingActive, setPollingActive] = useState(false);

    useEffect(() => {
        if (sessionStorage.getItem("isAuthenticated") !== "true") {
            navigate("/");
            return;
        }

        setLoadingMachines(true);
        getMachines()
            .then(data => {
                // Filtrar apenas máquinas com Mercado Pago ativado
                const mpMachines = data.filter(m => m.mercadoPagoEnabled);
                setMachines(mpMachines);
                if (mpMachines.length > 0) {
                    setSelectedMachineId(mpMachines[0].id);
                }
                setLoadingMachines(false);
            })
            .catch(err => {
                console.error("Erro ao carregar máquinas:", err);
                setLoadingMachines(false);
            });
    }, [navigate]);

    // Polling effect for telemetry logs
    useEffect(() => {
        if (!transaction?.transactionId || !pollingActive) return;

        const interval = setInterval(() => {
            getTransactionLogs(transaction.transactionId)
                .then(logs => {
                    setTelemetryLogs(logs);
                    
                    // Se o log indicar conclusão ou erro de estorno, interrompe o polling
                    const hasEnded = logs.some(l => 
                        l.message.includes("concluída com sucesso") || 
                        l.message.includes("Estorno Pix concluído") || 
                        l.message.includes("permaneceu offline") ||
                        l.message.includes("operador")
                    );
                    if (hasEnded) {
                        setPollingActive(false);
                    }
                })
                .catch(err => {
                    console.error("Erro ao carregar logs de telemetria:", err);
                });
        }, 2000);

        return () => clearInterval(interval);
    }, [transaction?.transactionId, pollingActive]);

    const handleCreateCharge = async (e: React.FormEvent) => {
        e.preventDefault();
        if (!selectedMachineId) {
            toast({
                title: "Aviso",
                description: "Selecione uma máquina de vendas.",
                variant: "destructive"
            });
            return;
        }

        const value = parseFloat(amount);
        if (isNaN(value) || value <= 0) {
            toast({
                title: "Valor Inválido",
                description: "O valor da venda deve ser maior que zero.",
                variant: "destructive"
            });
            return;
        }

        setGenerating(true);
        setTransaction(null);
        setTelemetryLogs([]);
        setPollingActive(false);

        try {
            const result = await createPixQrCode({
                machineId: selectedMachineId,
                amount: value,
                payerEmail,
                payerFirstName,
                payerLastName,
                payerCpf,
                useRealMercadoPago
            });

            if (result.applicationFeeApplied === false && !result.simulated) {
                throw new Error("Cobrança rejeitada: o QR Code Pix não será exibido sem application_fee.");
            }

            setTransaction(result);
            setPollingActive(true);
            toast({
                title: "Sucesso",
                description: "Cobrança Pix criada! Aguardando pagamento..."
            });
        } catch (err: any) {
            console.error("Erro ao criar cobrança:", err);
            if (err.transactionId) {
                setTransaction({ transactionId: err.transactionId, qrCode: "", qrCodeBase64: "", status: "Failed" });
                setPollingActive(true);
            }
            toast({
                title: "Erro na Operação",
                description: err.message || "Erro ao criar cobrança Pix no Mercado Pago.",
                variant: "destructive"
            });
        } finally {
            setGenerating(false);
        }
    };

    const handleSimulateWebhook = async (approved: boolean) => {
        if (!transaction) return;

        setSimulating(approved ? "approved" : "rejected");
        try {
            await simulateWebhook(transaction.transactionId, approved);
            toast({
                title: "Notificação Enviada",
                description: `Webhook de pagamento ${approved ? "aprovado" : "recusado"} disparado com sucesso!`
            });
            setPollingActive(true);
        } catch (err) {
            console.error("Erro ao simular webhook:", err);
            toast({
                title: "Erro",
                description: "Não foi possível simular o webhook.",
                variant: "destructive"
            });
        } finally {
            setSimulating(null);
        }
    };

    const copyToClipboard = () => {
        if (!transaction?.qrCode) return;
        navigator.clipboard.writeText(transaction.qrCode);
        setCopied(true);
        setTimeout(() => setCopied(false), 2000);
        toast({
            title: "Copiado!",
            description: "Código Pix Copia e Cola copiado para a área de transferência."
        });
    };

    const getLogBadgeClass = (type: string) => {
        switch (type) {
            case "CommandSent":
                return "bg-blue-100 text-blue-800 border-blue-200";
            case "AckReceived":
                return "bg-green-100 text-green-800 border-green-200";
            case "Error":
                return "bg-red-100 text-red-800 border-red-200";
            case "RefundTriggered":
                return "bg-purple-100 text-purple-800 border-purple-200";
            default:
                return "bg-slate-100 text-slate-800 border-slate-200";
        }
    };

    return (
        <SidebarProvider>
            <div className="min-h-screen flex w-full bg-slate-50/50">
                <AppSidebar />
                <div className="flex-1 flex flex-col min-w-0">
                    <header className="h-12 flex items-center border-b bg-white px-4 gap-3">
                        <SidebarTrigger />
                        <h1 className="text-sm font-semibold text-foreground flex items-center gap-2">
                            <QrCode className="w-4 h-4 text-indigo-600" />
                            Simulador de Cobrança (Pix MP)
                        </h1>
                    </header>

                    <main className="flex-1 p-6 space-y-6 overflow-auto max-w-6xl w-full mx-auto">
                        <div className="pb-4 border-b">
                            <h2 className="text-2xl font-bold tracking-tight">Simulação de Venda & Telemetria</h2>
                            <p className="text-muted-foreground">
                                Teste e homologue o fluxo completo de checkout transparente e ativação de telemetria MDB da máquina.
                            </p>
                        </div>

                        <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
                            {/* COLUNA 1: FORMULÁRIO DE GERAÇÃO */}
                            <Card className="border border-slate-200 bg-white shadow-sm lg:col-span-1">
                                <CardHeader className="pb-4 border-b border-slate-100">
                                    <CardTitle className="text-base font-bold flex items-center gap-2">
                                        <CreditCard className="w-4 h-4 text-indigo-600" />
                                        Nova Venda
                                    </CardTitle>
                                    <CardDescription>Configure os detalhes do pedido</CardDescription>
                                </CardHeader>
                                <CardContent className="p-6">
                                    {loadingMachines ? (
                                        <div className="space-y-3">
                                            <Skeleton className="h-10 w-full" />
                                            <Skeleton className="h-10 w-full" />
                                        </div>
                                    ) : machines.length === 0 ? (
                                        <div className="text-sm text-yellow-600 bg-yellow-50 border border-yellow-200 rounded p-4 text-center">
                                            Nenhuma máquina está com a cobrança via Mercado Pago habilitada. Ative-a na aba de Hardware de uma máquina.
                                        </div>
                                    ) : (
                                        <form onSubmit={handleCreateCharge} className="space-y-4">
                                            <div className="space-y-1.5">
                                                <label className="text-xs font-semibold text-slate-600">Selecione a Vending Machine</label>
                                                <Select value={selectedMachineId} onValueChange={setSelectedMachineId}>
                                                    <SelectTrigger>
                                                        <SelectValue placeholder="Escolha a máquina..." />
                                                    </SelectTrigger>
                                                    <SelectContent>
                                                        {machines.map(m => (
                                                            <SelectItem key={m.id} value={m.id}>
                                                                {m.name} ({m.serialNumber})
                                                            </SelectItem>
                                                        ))}
                                                    </SelectContent>
                                                </Select>
                                            </div>

                                            <div className="space-y-1.5">
                                                <label className="text-xs font-semibold text-slate-600">Valor da Venda (R$)</label>
                                                <Input
                                                    type="number"
                                                    step="0.01"
                                                    min="0.05"
                                                    value={amount}
                                                    onChange={e => setAmount(e.target.value)}
                                                    className="font-mono-data text-base font-bold"
                                                />
                                            </div>



                                            <div className="space-y-3 rounded-lg border border-slate-200/80 bg-slate-50 p-3">
                                                <p className="text-xs font-bold text-slate-700">Dados do pagador</p>
                                                <Input
                                                    type="email"
                                                    placeholder="E-mail do pagador"
                                                    value={payerEmail}
                                                    onChange={e => setPayerEmail(e.target.value)}
                                                    className="h-9 bg-white text-sm"
                                                />
                                                <div className="grid grid-cols-2 gap-2">
                                                    <Input
                                                        placeholder="Nome"
                                                        value={payerFirstName}
                                                        onChange={e => setPayerFirstName(e.target.value)}
                                                        className="h-9 bg-white text-sm"
                                                    />
                                                    <Input
                                                        placeholder="Sobrenome"
                                                        value={payerLastName}
                                                        onChange={e => setPayerLastName(e.target.value)}
                                                        className="h-9 bg-white text-sm"
                                                    />
                                                </div>
                                                <Input
                                                    placeholder="CPF ou CNPJ do pagador"
                                                    value={payerCpf}
                                                    onChange={e => setPayerCpf(e.target.value)}
                                                    className="h-9 bg-white text-sm"
                                                />
                                            </div>

                                            <div className="flex items-center justify-between p-3 bg-slate-50 border border-slate-200/80 rounded-lg mt-2">
                                                <div className="space-y-0.5 pr-2">
                                                    <label htmlFor="real-mp" className="text-xs font-bold text-slate-700 cursor-pointer">Utilizar Mercado Pago Real</label>
                                                    <p className="text-[9px] text-muted-foreground leading-snug">Chama a API oficial do Mercado Pago usando as credenciais ativas da empresa dona da máquina.</p>
                                                </div>
                                                <Switch 
                                                    id="real-mp"
                                                    checked={useRealMercadoPago}
                                                    onCheckedChange={setUseRealMercadoPago}
                                                />
                                            </div>

                                            <Button type="submit" disabled={generating} className="w-full bg-indigo-600 hover:bg-indigo-700 gap-2 mt-4 font-semibold">
                                                {generating ? (
                                                    <>
                                                        <RefreshCw className="w-4 h-4 animate-spin" /> Gerando...
                                                    </>
                                                ) : (
                                                    <>
                                                        <Play className="w-4 h-4" /> Gerar QR Code Pix
                                                    </>
                                                )}
                                            </Button>
                                        </form>
                                    )}
                                </CardContent>
                            </Card>

                            {/* COLUNA 2: QR CODE E CONTROLES DE WEBHOOK */}
                            <Card className="border border-slate-200 bg-white shadow-sm lg:col-span-1">
                                <CardHeader className="pb-4 border-b border-slate-100">
                                    <CardTitle className="text-base font-bold flex items-center gap-2">
                                        <QrCode className="w-4 h-4 text-indigo-600" />
                                        QR Code Pix
                                    </CardTitle>
                                    <CardDescription>Aguardando solicitação</CardDescription>
                                </CardHeader>
                                <CardContent className="p-6 flex flex-col items-center justify-center min-h-[300px]">
                                    {!transaction ? (
                                        <div className="text-center text-slate-400 py-12">
                                            <QrCode className="w-16 h-16 mx-auto stroke-1 opacity-40 mb-3" />
                                            <p className="text-sm font-medium">Preencha os dados e clique em "Gerar QR Code Pix" para começar.</p>
                                        </div>
                                    ) : (
                                        <div className="w-full space-y-6 flex flex-col items-center">
                                            {/* Render QR Code base64 */}
                                            <div className="border p-4 bg-white rounded-lg shadow-sm">
                                                <img 
                                                    src={`data:image/png;base64,${transaction.qrCodeBase64}`} 
                                                    alt="Pix QR Code" 
                                                    className="w-44 h-44 select-none"
                                                />
                                            </div>

                                            <div className="w-full space-y-2">
                                                <p className="text-xs font-semibold text-slate-600 text-center">Código Copia e Cola:</p>
                                                <div className="flex gap-1.5">
                                                    <Input 
                                                        readOnly 
                                                        value={transaction.qrCode} 
                                                        className="font-mono text-[10px] h-8 bg-slate-50 overflow-ellipsis"
                                                    />
                                                    <Button size="icon" variant="outline" className="h-8 w-8 shrink-0" onClick={copyToClipboard}>
                                                        {copied ? <Check className="w-4 h-4 text-green-600" /> : <Copy className="w-4 h-4" />}
                                                    </Button>
                                                </div>
                                            </div>

                                            <hr className="w-full border-slate-100" />
                                            
                                            {/* SIMULAR RETORNO DO PAGAMENTO */}
                                            <div className="w-full space-y-3">
                                                <p className="text-xs font-bold text-slate-700 text-center uppercase tracking-wider">Simular Resposta (Webhook)</p>
                                                <div className="grid grid-cols-2 gap-3">
                                                    <Button 
                                                        onClick={() => handleSimulateWebhook(true)}
                                                        disabled={simulating !== null}
                                                        className="bg-green-600 hover:bg-green-700 font-semibold gap-1.5"
                                                    >
                                                        <CheckCircle className="w-4 h-4" /> Aprovado
                                                    </Button>
                                                    <Button 
                                                        onClick={() => handleSimulateWebhook(false)}
                                                        disabled={simulating !== null}
                                                        variant="destructive"
                                                        className="font-semibold gap-1.5"
                                                    >
                                                        <XCircle className="w-4 h-4" /> Recusado
                                                    </Button>
                                                </div>
                                            </div>
                                        </div>
                                    )}
                                </CardContent>
                            </Card>

                            {/* COLUNA 3: LOG DE TELEMETRIA MDB REMOTA EM TEMPO REAL */}
                            <Card className="border border-slate-200 bg-white shadow-sm lg:col-span-1">
                                <CardHeader className="pb-4 border-b border-slate-100 flex flex-row items-center justify-between">
                                    <div>
                                        <CardTitle className="text-base font-bold flex items-center gap-2">
                                            <Terminal className="w-4 h-4 text-indigo-600" />
                                            Log de Telemetria
                                        </CardTitle>
                                        <CardDescription>Mensagens MDB em tempo real</CardDescription>
                                    </div>
                                    {pollingActive && (
                                        <span className="flex h-2 w-2 relative">
                                            <span className="animate-ping absolute inline-flex h-full w-full rounded-full bg-indigo-400 opacity-75"></span>
                                            <span className="relative inline-flex rounded-full h-2 w-2 bg-indigo-500"></span>
                                        </span>
                                    )}
                                </CardHeader>
                                <CardContent className="p-4 bg-slate-950 text-slate-300 font-mono text-xs rounded-b-xl min-h-[350px] max-h-[450px] overflow-y-auto flex flex-col gap-2 border-t shadow-inner">
                                    {telemetryLogs.length === 0 ? (
                                        <div className="text-slate-500 italic py-8 text-center select-none">
                                            Aguardando ativação do webhook para iniciar o monitoramento MDB...
                                        </div>
                                    ) : (
                                        telemetryLogs.map((log) => (
                                            <div key={log.id} className="pb-2 border-b border-slate-800/50 last:border-0">
                                                <div className="flex justify-between items-center mb-1 text-[10px]">
                                                    <span className="text-slate-500">
                                                        {new Date(log.timestamp).toLocaleTimeString("pt-BR")}
                                                    </span>
                                                    <span className={`px-1.5 py-0.2 rounded border text-[9px] font-bold ${getLogBadgeClass(log.logType)}`}>
                                                        {log.logType}
                                                    </span>
                                                </div>
                                                <p className="text-slate-200 leading-relaxed break-words">{log.message}</p>
                                            </div>
                                        ))
                                    )}
                                </CardContent>
                            </Card>
                        </div>
                    </main>
                </div>
            </div>
        </SidebarProvider>
    );
}
