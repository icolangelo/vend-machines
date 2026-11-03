import { useCallback, useEffect, useRef, useState } from "react";
import { CreditCard, RefreshCw } from "lucide-react";
import { useSearchParams } from "react-router-dom";
import { AppSidebar } from "@/components/AppSidebar";
import { SidebarProvider, SidebarTrigger } from "@/components/ui/sidebar";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { getMachines, getPaymentTransactionHistory, getTransactionLogs, type PaymentTransaction, type TransactionTelemetryLog, type TransactionTelemetryLogPage } from "@/lib/api";
import type { Machine } from "@/data/mockData";
import { toast } from "sonner";

const statusLabels: Record<string, string> = {
    Pending: "Pendente",
    Approved: "Aprovado",
    Rejected: "Recusado",
    Failed: "Falhou",
    Refunded: "Estornado",
    Cancelled: "Cancelado",
    ReconciliationRequired: "Conciliação necessária",
    Chargeback: "Contestado (chargeback)",
};

const providerStatusLabels: Record<string, string> = {
    pending: "Pendente",
    approved: "Aprovado",
    authorized: "Autorizado",
    in_process: "Em processamento",
    in_mediation: "Em mediação",
    rejected: "Recusado",
    cancelled: "Cancelado",
    refunded: "Estornado",
    charged_back: "Contestado (chargeback)",
    partially_refunded: "Parcialmente estornado",
};

const providerDetailLabels: Record<string, string> = {
    accredited: "Credenciado",
    pending_contingency: "Aguardando processamento",
    pending_review_manual: "Aguardando análise manual",
    cc_rejected_bad_filled_other: "Recusado: dados de pagamento não aceitos",
    cc_rejected_call_for_authorize: "Recusado: autorização necessária",
    cc_rejected_card_disabled: "Recusado: cartão desabilitado",
    cc_rejected_duplicated_payment: "Recusado: possível cobrança duplicada",
    cc_rejected_insufficient_amount: "Recusado: saldo insuficiente",
    cc_rejected_invalid_installments: "Recusado: parcelamento inválido",
    cc_rejected_max_attempts: "Recusado: limite de tentativas atingido",
    cc_rejected_other_reason: "Recusado: motivo informado pelo provedor",
};

const providerLabel = (value: string, labels: Record<string, string>) => labels[value.toLowerCase()] ?? "Outro estado";

const logTypeLabels: Record<TransactionTelemetryLog["logType"], string> = {
    Info: "Informação",
    CommandSent: "Comando enviado",
    AckReceived: "Confirmação recebida",
    Error: "Erro",
    RefundTriggered: "Estorno solicitado",
};

const formatCurrency = (value: number) => new Intl.NumberFormat("pt-BR", {
    style: "currency",
    currency: "BRL",
}).format(value);

const formatDate = (value?: string) => value
    ? new Date(value).toLocaleString("pt-BR")
    : "Ainda não concluído";

const PAYMENT_HISTORY_PAGE_SIZE = 25;

export default function Payments() {
    const [searchParams, setSearchParams] = useSearchParams();
    const transactionId = searchParams.get("transactionId") ?? "";
    const selectedMachineId = searchParams.get("machineId") ?? "all";
    const selectedStatus = searchParams.get("status") ?? "all";
    const [machines, setMachines] = useState<Machine[]>([]);
    const [transactions, setTransactions] = useState<PaymentTransaction[]>([]);
    const [page, setPage] = useState(1);
    const [loadedPage, setLoadedPage] = useState(1);
    const [totalTransactions, setTotalTransactions] = useState(0);
    const [totalPages, setTotalPages] = useState(1);
    const [loading, setLoading] = useState(true);
    const [loadError, setLoadError] = useState(false);
    const [expandedTransactionId, setExpandedTransactionId] = useState<string | null>(null);
    const [transactionLogs, setTransactionLogs] = useState<Record<string, TransactionTelemetryLogPage>>({});
    const [logsLoadingId, setLogsLoadingId] = useState<string | null>(null);
    const [logsErrorId, setLogsErrorId] = useState<string | null>(null);
    const transactionRequestVersion = useRef(0);

    const loadTransactions = useCallback(async () => {
        const currentRequest = ++transactionRequestVersion.current;
        setLoading(true);
        setLoadError(false);
        try {
            const result = await getPaymentTransactionHistory(
                page,
                PAYMENT_HISTORY_PAGE_SIZE,
                transactionId ? undefined : selectedMachineId === "all" ? undefined : selectedMachineId,
                transactionId || undefined,
                transactionId || selectedStatus === "all" ? undefined : selectedStatus,
            );
            if (currentRequest !== transactionRequestVersion.current) return;
            setTransactions(result.items);
            setLoadedPage(result.page);
            setTotalTransactions(result.total);
            setTotalPages(result.totalPages);
        } catch (error) {
            if (currentRequest !== transactionRequestVersion.current) return;
            console.error("Erro ao carregar histórico de pagamentos:", error);
            setLoadError(true);
            toast.error("Não foi possível carregar o histórico de pagamentos.");
        } finally {
            if (currentRequest === transactionRequestVersion.current) setLoading(false);
        }
    }, [page, selectedMachineId, selectedStatus, transactionId]);

    useEffect(() => {
        let active = true;
        getMachines()
            .then(items => { if (active) setMachines(items); })
            .catch(error => console.error("Erro ao carregar máquinas para filtrar pagamentos:", error));
        return () => { active = false; };
    }, []);

    useEffect(() => {
        void loadTransactions();
        return () => { transactionRequestVersion.current += 1; };
    }, [loadTransactions]);

    const loadTransactionLogs = async (id: string, page = 1, append = false) => {
        setLogsLoadingId(id);
        setLogsErrorId(null);
        try {
            const response = page === 1
                ? await getTransactionLogs(id)
                : await getTransactionLogs(id, page, 50);
            const result: TransactionTelemetryLogPage = Array.isArray(response)
                ? { items: response as TransactionTelemetryLog[], total: response.length, page: 1, pageSize: 50, totalPages: 1 }
                : response;
            setTransactionLogs(current => ({
                ...current,
                [id]: append && current[id]
                    ? { ...result, items: [...current[id].items, ...result.items] }
                    : result,
            }));
        } catch (error) {
            console.error("Erro ao carregar eventos da transação:", error);
            setLogsErrorId(id);
        } finally {
            setLogsLoadingId(current => current === id ? null : current);
        }
    };

    const toggleTransactionLogs = async (transaction: PaymentTransaction) => {
        if (expandedTransactionId === transaction.id) {
            setExpandedTransactionId(null);
            return;
        }
        setExpandedTransactionId(transaction.id);
        if (transactionLogs[transaction.id]) return;
        await loadTransactionLogs(transaction.id);
    };

    return (
        <SidebarProvider>
            <div className="min-h-screen flex w-full">
                <AppSidebar />
                <div className="flex-1 flex min-w-0 flex-col bg-background">
                    <header className="h-12 flex items-center border-b bg-card px-4 gap-3">
                        <SidebarTrigger />
                        <div className="flex items-center gap-2">
                            <CreditCard className="h-4 w-4 text-primary" />
                            <h1 className="text-sm font-semibold text-foreground">Pagamentos</h1>
                        </div>
                    </header>
                    <main className="flex-1 p-4 md:p-6 space-y-5 max-w-7xl w-full mx-auto">
                        <div className="flex flex-wrap items-start justify-between gap-3">
                            <div>
                                <h2 className="text-xl font-bold">Histórico de transações</h2>
                                <p className="mt-1 text-sm text-muted-foreground">
                                    {transactionId ? "Detalhes da transação associada ao alerta." : "Consulte o histórico de transações da sua empresa."}
                                </p>
                            </div>
                            <Button variant="outline" onClick={() => void loadTransactions()} disabled={loading}>
                                <RefreshCw className={`mr-2 h-4 w-4 ${loading ? "animate-spin" : ""}`} />
                                Atualizar
                            </Button>
                        </div>

                        {loadError && (
                            <div role="alert" className="flex flex-wrap items-center justify-between gap-3 rounded-md border border-destructive/30 bg-destructive/5 p-3 text-sm">
                                <span>{transactions.length ? "Não foi possível atualizar. Os dados exibidos podem estar desatualizados." : "O histórico está indisponível."}</span>
                                <Button variant="outline" size="sm" onClick={() => void loadTransactions()} disabled={loading}>Tentar novamente</Button>
                            </div>
                        )}

                        <Card>
                            <CardHeader>
                                <CardTitle className="text-base">Transações</CardTitle>
                                <CardDescription>Valores e eventos são consultados no escopo da empresa autenticada. Uma cobrança não representa uma venda até a entrega confirmada; os relatórios de vendas usam o razão de entregas.</CardDescription>
                            </CardHeader>
                            <CardContent className="space-y-3">
                                {!transactionId && (
                                    <div className="flex flex-col justify-end gap-2 pb-2 sm:flex-row">
                                        <div className="w-full sm:w-72">
                                            <Select
                                                value={selectedMachineId}
                                                onValueChange={value => {
                                                    setPage(1);
                                                    setSearchParams(current => {
                                                        const next = new URLSearchParams(current);
                                                        if (value === "all") next.delete("machineId");
                                                        else next.set("machineId", value);
                                                        return next;
                                                    });
                                                }}
                                            >
                                                <SelectTrigger aria-label="Filtrar pagamentos por máquina">
                                                    <SelectValue placeholder="Todas as máquinas" />
                                                </SelectTrigger>
                                                <SelectContent>
                                                    <SelectItem value="all">Todas as máquinas</SelectItem>
                                                    {machines.map(machine => <SelectItem key={machine.id} value={machine.id}>{machine.name} · {machine.id}</SelectItem>)}
                                                </SelectContent>
                                            </Select>
                                        </div>
                                        <div className="w-full sm:w-60">
                                            <Select
                                                value={selectedStatus}
                                                onValueChange={value => {
                                                    setPage(1);
                                                    setSearchParams(current => {
                                                        const next = new URLSearchParams(current);
                                                        if (value === "all") next.delete("status");
                                                        else next.set("status", value);
                                                        return next;
                                                    });
                                                }}
                                            >
                                                <SelectTrigger aria-label="Filtrar pagamentos por estado">
                                                    <SelectValue placeholder="Todos os estados" />
                                                </SelectTrigger>
                                                <SelectContent>
                                                    <SelectItem value="all">Todos os estados</SelectItem>
                                                    {Object.entries(statusLabels).map(([value, label]) => <SelectItem key={value} value={value}>{label}</SelectItem>)}
                                                </SelectContent>
                                            </Select>
                                        </div>
                                    </div>
                                )}
                                {loading && transactions.length === 0 ? (
                                    <p className="py-8 text-center text-sm text-muted-foreground">Carregando transações...</p>
                                ) : loadError && transactions.length === 0 ? (
                                    <p className="py-8 text-center text-sm text-muted-foreground">Não foi possível consultar as transações.</p>
                                ) : transactions.length === 0 ? (
                                    <p className="py-8 text-center text-sm text-muted-foreground">Nenhuma transação encontrada.</p>
                                ) : transactions.map(transaction => (
                                    <article key={transaction.id} className="rounded-lg border p-4">
                                        <div className="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
                                            <div className="space-y-1">
                                                <div className="flex flex-wrap items-center gap-2">
                                                    <span className="font-semibold">{transaction.machine?.name || transaction.machineId}</span>
                                                    <Badge variant={transaction.status === "Approved" ? "default" : transaction.status === "Failed" || transaction.status === "Rejected" || transaction.status === "Cancelled" || transaction.status === "Chargeback" || transaction.status === "ReconciliationRequired" ? "destructive" : "secondary"}>
                                                        {statusLabels[transaction.status] ?? "Outro estado"}
                                                    </Badge>
                                                </div>
                                                <p className="text-xs text-muted-foreground">Criada em {formatDate(transaction.createdAt)}</p>
                                                <p className="text-xs text-muted-foreground">Concluída em {formatDate(transaction.completedAt)}</p>
                                                <p className="break-all text-xs text-muted-foreground">ID: {transaction.id}</p>
                                                {transaction.mercadoPagoStatus && <p className="text-xs text-muted-foreground">Estado no provedor: {providerLabel(transaction.mercadoPagoStatus, providerStatusLabels)}{transaction.mercadoPagoStatusDetail ? ` · ${providerLabel(transaction.mercadoPagoStatusDetail, providerDetailLabels)}` : ""}</p>}
                                            </div>
                                            <div className="sm:text-right">
                                                <p className="text-lg font-semibold">{formatCurrency(transaction.amount)}</p>
                                                <p className="text-xs text-muted-foreground">Valor cobrado</p>
                                                <p className="text-xs text-muted-foreground">Taxa da plataforma: {formatCurrency(transaction.applicationFee)}</p>
                                                <p className="mt-1 text-sm font-medium">Após taxa da plataforma: {formatCurrency(transaction.amount - transaction.applicationFee)}</p>
                                                <p className="text-xs text-muted-foreground">Antes de tarifas próprias do Mercado Pago</p>
                                            </div>
                                        </div>
                                        <div className="mt-3 border-t pt-3">
                                            <Button variant="ghost" size="sm" onClick={() => void toggleTransactionLogs(transaction)}>
                                                {expandedTransactionId === transaction.id ? "Ocultar eventos" : "Ver eventos da transação"}
                                            </Button>
                                            {expandedTransactionId === transaction.id && (
                                                <div className="mt-2 rounded-md bg-muted/40 p-3">
                                                    {logsLoadingId === transaction.id ? (
                                                        <p className="text-sm text-muted-foreground">Carregando eventos...</p>
                                                    ) : logsErrorId === transaction.id ? (
                                                        <div className="flex flex-wrap items-center justify-between gap-2 text-sm">
                                                            <span className="text-destructive">Não foi possível carregar os eventos.</span>
                                                            <Button variant="outline" size="sm" onClick={() => void loadTransactionLogs(transaction.id)} disabled={logsLoadingId === transaction.id}>Tentar novamente</Button>
                                                        </div>
                                                    ) : (transactionLogs[transaction.id]?.items ?? []).length === 0 ? (
                                                        <p className="text-sm text-muted-foreground">Nenhum evento registrado.</p>
                                                    ) : (
                                                        <>
                                                            <ol className="space-y-2">
                                                                {transactionLogs[transaction.id].items.map(log => (
                                                                    <li key={log.id} className="border-l-2 border-border pl-3 text-sm">
                                                                        <p className="font-medium">{logTypeLabels[log.logType] ?? "Outro evento"}</p>
                                                                        <p className="text-xs text-muted-foreground">{formatDate(log.timestamp)}</p>
                                                                        <p className="break-words text-muted-foreground">{log.message}</p>
                                                                    </li>
                                                                ))}
                                                            </ol>
                                                            {transactionLogs[transaction.id].page < transactionLogs[transaction.id].totalPages && (
                                                                <Button variant="outline" size="sm" className="mt-3" onClick={() => void loadTransactionLogs(transaction.id, transactionLogs[transaction.id].page + 1, true)} disabled={logsLoadingId === transaction.id}>
                                                                    {logsLoadingId === transaction.id ? "Carregando..." : "Carregar eventos anteriores"}
                                                                </Button>
                                                            )}
                                                        </>
                                                    )}
                                                </div>
                                            )}
                                        </div>
                                    </article>
                                ))}
                                {totalTransactions > 0 && !transactionId && (
                                    <div className="flex flex-col gap-3 border-t pt-4 sm:flex-row sm:items-center sm:justify-between" aria-label="Paginação do histórico de pagamentos">
                                        <p className="text-sm text-muted-foreground">
                                            Exibindo {(loadedPage - 1) * PAYMENT_HISTORY_PAGE_SIZE + 1}–{Math.min(loadedPage * PAYMENT_HISTORY_PAGE_SIZE, totalTransactions)} de {totalTransactions} transações
                                        </p>
                                        <div className="flex items-center gap-2">
                                            <Button variant="outline" size="sm" onClick={() => setPage(current => Math.max(1, loadedPage - 1))} disabled={loading || loadedPage <= 1}>Anterior</Button>
                                            <span className="text-sm text-muted-foreground">Página {loadedPage} de {totalPages}</span>
                                            <Button variant="outline" size="sm" onClick={() => setPage(loadedPage + 1)} disabled={loading || loadedPage >= totalPages}>Próxima</Button>
                                        </div>
                                    </div>
                                )}
                            </CardContent>
                        </Card>
                    </main>
                </div>
            </div>
        </SidebarProvider>
    );
}
