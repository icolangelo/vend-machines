import { SidebarProvider, SidebarTrigger } from "@/components/ui/sidebar";
import { AppSidebar } from "@/components/AppSidebar";
import { useAuth } from "@/contexts/useAuth";
import { useNavigate, useSearchParams } from "react-router-dom";
import { useEffect, useRef, useState } from "react";
import { getUsers, updateUserRole, getCompanies, updateCompanyActivation, getGlobalSettings, updateGlobalSettings, getCompanyIntegration, getAdminLogs, getDeviceMessageInbox, getPaymentWebhookInbox, getReconciliationQueue, getWorkerHealth, getLegacyMachineConnections, retryDeviceMessageInbox, retryPaymentWebhook, resolvePaymentReconciliation, type PaginatedUsers, type PaginatedCompanies, type SystemSettings, type MercadoPagoIntegration, type PaginatedAdminLogs, type AdminLog, type DeviceMessageInboxSummary, type PaymentWebhookInboxSummary, type PaymentWebhookStatus, type ReconciliationQueueSummary, type ReconciliationQueueItem, type PaymentReconciliationResolutionRequest, type WorkerHealthSummary, type LegacyMachineConnectionsSummary } from "@/lib/api";
import { 
    Users as UsersIcon, 
    ShieldAlert, 
    LogOut, 
    Search, 
    ChevronLeft, 
    ChevronRight,
    Building2,
    Activity,
    CreditCard,
    Terminal,
    Save,
    RefreshCw,
    RotateCw
} from "lucide-react";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Input } from "@/components/ui/input";
import { Button } from "@/components/ui/button";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Skeleton } from "@/components/ui/skeleton";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Badge } from "@/components/ui/badge";

const getRoleLabel = (role?: string) => {
    if (role === "PlatformAdmin") return "Administrador da plataforma";
    if (role === "Admin") return "Administrador";
    if (role === "Operator") return "Operador";
    return role ?? "";
};

const workerLabels: Record<string, string> = {
    "telemetry-inbox": "Processamento da inbox MDB",
    "telemetry-panel-event-relay": "Eventos do painel entre instâncias",
    "telemetry-command-dispatch": "Roteamento de comandos MDB",
    "telemetry-inbox-consumer-1": "Consumidor MDB 1",
    "telemetry-inbox-consumer-2": "Consumidor MDB 2",
    "telemetry-inbox-consumer-3": "Consumidor MDB 3",
    "telemetry-inbox-consumer-4": "Consumidor MDB 4",
    "mercado-pago-webhook": "Webhooks Mercado Pago",
    "mercado-pago-reconciliation": "Conciliação Mercado Pago",
    "operational-alerts": "Sincronização de alertas",
    "telemetry-deadlines": "Prazos de telemetria",
    "mdb-status-polling": "Consulta de status MDB",
};

const workerStatusLabels: Record<string, string> = {
    Healthy: "Saudável",
    Degraded: "Com falhas",
    Stale: "Sem atualização",
    Stopped: "Parado",
    NotStarted: "Não iniciado",
};

const paymentWebhookStatusLabels: Record<PaymentWebhookStatus, string> = {
    Pending: "Pendente",
    Retry: "Nova tentativa",
    Processing: "Processando",
    Processed: "Processado",
    Failed: "Falha final",
};

const reconciliationReasonLabels: Record<string, string> = {
    provider_approval_after_financial_reversal: "Aprovação após estorno ou chargeback",
    provider_approval_after_rejected_or_cancelled: "Aprovação após cobrança recusada ou cancelada",
    provider_regression_after_approved: "Pagamento aprovado mudou para um estado incompatível",
    provider_regression_after_refunded: "Pagamento estornado mudou para um estado incompatível",
    provider_regression_after_chargeback: "Chargeback mudou para um estado incompatível",
    chargeback_reimbursement_amount_missing: "Valor do reembolso do chargeback não informado",
    provider_in_mediation: "Pagamento em mediação no Mercado Pago",
};

const getReconciliationResolution = (item: ReconciliationQueueItem): Pick<PaymentReconciliationResolutionRequest, "resolution"> & {
    label: string;
    requiresRefundAmount: boolean;
    minimumRefundedAmount: number;
    refundAmountLabel: string;
} | null => {
    const status = item.mercadoPagoStatus?.toLowerCase();
    const detail = item.mercadoPagoStatusDetail?.toLowerCase();
    if (status === "approved" && detail === "accredited")
        return { resolution: "approved", label: "Aprovar pagamento confirmado", requiresRefundAmount: false, minimumRefundedAmount: 0, refundAmountLabel: "" };
    if (status === "approved" && detail === "partially_refunded")
        return { resolution: "approved", label: "Confirmar pagamento com estorno parcial", requiresRefundAmount: true, minimumRefundedAmount: 0.01, refundAmountLabel: "Valor estornado confirmado (R$)" };
    if (status === "rejected")
        return { resolution: "rejected", label: "Confirmar pagamento rejeitado", requiresRefundAmount: false, minimumRefundedAmount: 0, refundAmountLabel: "" };
    if (status === "cancelled")
        return { resolution: "cancelled", label: "Confirmar pagamento cancelado", requiresRefundAmount: false, minimumRefundedAmount: 0, refundAmountLabel: "" };
    if (status === "refunded")
        return { resolution: "refunded", label: "Confirmar estorno integral", requiresRefundAmount: false, minimumRefundedAmount: 0, refundAmountLabel: "" };
    if (status === "charged_back" && detail === "settled")
        return { resolution: "chargeback_settled", label: "Confirmar chargeback liquidado", requiresRefundAmount: false, minimumRefundedAmount: 0, refundAmountLabel: "" };
    if (status === "charged_back" && detail === "reimbursed")
        return { resolution: "chargeback_reimbursed", label: "Confirmar chargeback reembolsado à empresa", requiresRefundAmount: true, minimumRefundedAmount: 0, refundAmountLabel: "Valor estornado confirmado (R$)" };
    return null;
};

const getWorkerDisplayName = (name: string) => workerLabels[name] ?? `Worker não identificado (${name})`;
const getWorkerStatusLabel = (status: string) => workerStatusLabels[status] ?? "Desconhecido";

const formatAge = (seconds?: number | null) => {
    if (seconds == null) return "—";
    if (seconds < 60) return `${seconds} s`;
    if (seconds < 3600) return `${Math.floor(seconds / 60)} min`;
    return `${Math.floor(seconds / 3600)} h ${Math.floor((seconds % 3600) / 60)} min`;
};

const formatLeaseExpiry = (expiry?: string | null) => {
    if (!expiry) return "lease não registrado";
    const expiryTime = new Date(expiry).getTime();
    if (!Number.isFinite(expiryTime)) return "horário do lease indisponível";
    const secondsUntilExpiry = Math.floor((expiryTime - Date.now()) / 1000);
    return secondsUntilExpiry <= 0
        ? `lease vencido há ${formatAge(Math.abs(secondsUntilExpiry))}`
        : `lease vence em ${formatAge(secondsUntilExpiry)}`;
};

export default function Admin() {
    const { user, logout } = useAuth();
    const navigate = useNavigate();
    const [searchParams] = useSearchParams();
    const requestedTab = searchParams.get("tab");
    const initialTab = ["users", "companies", "payments", "payment-webhooks", "logs", "telemetry-inbox", "operational-health"].includes(requestedTab ?? "")
        ? requestedTab!
        : "users";

    // Estados da listagem de usuários
    const [userData, setUserData] = useState<PaginatedUsers | null>(null);
    const [userPage, setUserPage] = useState(1);
    const [userSearch, setUserSearch] = useState("");
    const [userSearchInput, setUserSearchInput] = useState("");
    const [usersLoading, setUsersLoading] = useState(true);
    const [usersError, setUsersError] = useState(false);
    const [usersRefreshVersion, setUsersRefreshVersion] = useState(0);
    const [confirmedUsersQuery, setConfirmedUsersQuery] = useState<{ page: number; search: string; version: number } | null>(null);
    const [roleChangeUserId, setRoleChangeUserId] = useState<string | null>(null);

    // Estados da listagem de empresas
    const [companyData, setCompanyData] = useState<PaginatedCompanies | null>(null);
    const [companyPage, setCompanyPage] = useState(1);
    const [companySearch, setCompanySearch] = useState("");
    const [companySearchInput, setCompanySearchInput] = useState("");
    const [companiesLoading, setCompaniesLoading] = useState(true);
    const [companiesError, setCompaniesError] = useState(false);
    const [companiesRefreshVersion, setCompaniesRefreshVersion] = useState(0);
    const [confirmedCompaniesQuery, setConfirmedCompaniesQuery] = useState<{ page: number; search: string; version: number } | null>(null);
    const [companyActivationId, setCompanyActivationId] = useState<string | null>(null);

    const isAdmin = user?.role === "PlatformAdmin";
    const usersCurrent = !!userData && !usersLoading && !usersError && confirmedUsersQuery?.page === userPage &&
        confirmedUsersQuery.search === userSearch && confirmedUsersQuery.version === usersRefreshVersion;
    const companiesCurrent = !!companyData && !companiesLoading && !companiesError && confirmedCompaniesQuery?.page === companyPage &&
        confirmedCompaniesQuery.search === companySearch && confirmedCompaniesQuery.version === companiesRefreshVersion;

    const [globalSettings, setGlobalSettings] = useState<SystemSettings | null>(null);
    const [settingsLoading, setSettingsLoading] = useState(false);
    const [settingsError, setSettingsError] = useState(false);
    const [settingsErrorMessage, setSettingsErrorMessage] = useState("");
    const [settingsRefreshVersion, setSettingsRefreshVersion] = useState(0);
    const [confirmedSettingsVersion, setConfirmedSettingsVersion] = useState<number | null>(null);
    const [savingSettings, setSavingSettings] = useState(false);
    const [feeInput, setFeeInput] = useState<number>(5.0);
    const settingsCurrent = globalSettings !== null && confirmedSettingsVersion === settingsRefreshVersion && !settingsLoading && !settingsError;

    // Detalhes da Integração da Empresa (Admin)
    const [isDetailsOpen, setIsDetailsOpen] = useState(false);
    const [selectedCompanyForDetails, setSelectedCompanyForDetails] = useState<{ id: string, name: string } | null>(null);
    const [companyIntegration, setCompanyIntegration] = useState<MercadoPagoIntegration | null>(null);
    const [loadingDetails, setLoadingDetails] = useState(false);
    const [integrationDetailsError, setIntegrationDetailsError] = useState(false);
    const integrationRequestVersion = useRef(0);
    useEffect(() => () => { integrationRequestVersion.current += 1; }, []);

    // Estados dos Logs (Admin)
    const [logsData, setLogsData] = useState<PaginatedAdminLogs | null>(null);
    const [logsPage, setLogsPage] = useState(1);
    const [logsSearch, setLogsSearch] = useState("");
    const [logsSearchInput, setLogsSearchInput] = useState("");
    const [logsLoading, setLogsLoading] = useState(true);
    const [logsError, setLogsError] = useState(false);
    const [logsRefreshVersion, setLogsRefreshVersion] = useState(0);
    const [selectedLogForDetails, setSelectedLogForDetails] = useState<AdminLog | null>(null);
    const [isLogDetailsOpen, setIsLogDetailsOpen] = useState(false);

    const [inboxData, setInboxData] = useState<DeviceMessageInboxSummary | null>(null);
    const [inboxPage, setInboxPage] = useState(1);
    const [inboxLoading, setInboxLoading] = useState(false);
    const [inboxError, setInboxError] = useState<string | null>(null);
    const [inboxRetryingId, setInboxRetryingId] = useState<string | null>(null);
    const [inboxRefreshVersion, setInboxRefreshVersion] = useState(0);
    const [confirmedInboxQuery, setConfirmedInboxQuery] = useState<{ page: number; version: number } | null>(null);
    const inboxCurrent = confirmedInboxQuery?.page === inboxPage && confirmedInboxQuery.version === inboxRefreshVersion && !inboxLoading && !inboxError;
    const [paymentWebhookData, setPaymentWebhookData] = useState<PaymentWebhookInboxSummary | null>(null);
    const [paymentWebhookStatus, setPaymentWebhookStatus] = useState<PaymentWebhookStatus | "All">("Failed");
    const [paymentWebhookPage, setPaymentWebhookPage] = useState(1);
    const [paymentWebhookLoading, setPaymentWebhookLoading] = useState(false);
    const [paymentWebhookError, setPaymentWebhookError] = useState<string | null>(null);
    const [paymentWebhookRetryingId, setPaymentWebhookRetryingId] = useState<string | null>(null);
    const [paymentWebhookRefreshVersion, setPaymentWebhookRefreshVersion] = useState(0);
    const [confirmedWebhookQuery, setConfirmedWebhookQuery] = useState<{ page: number; status: PaymentWebhookStatus | "All"; version: number } | null>(null);
    const webhookCurrent = confirmedWebhookQuery?.page === paymentWebhookPage && confirmedWebhookQuery.status === paymentWebhookStatus && confirmedWebhookQuery.version === paymentWebhookRefreshVersion && !paymentWebhookLoading && !paymentWebhookError;
    const [reconciliationData, setReconciliationData] = useState<ReconciliationQueueSummary | null>(null);
    const [reconciliationPage, setReconciliationPage] = useState(1);
    const [reconciliationLoading, setReconciliationLoading] = useState(false);
    const [reconciliationError, setReconciliationError] = useState<string | null>(null);
    const [reconciliationRefreshVersion, setReconciliationRefreshVersion] = useState(0);
    const [confirmedReconciliationQuery, setConfirmedReconciliationQuery] = useState<{ page: number; version: number } | null>(null);
    const [reconciliationItemToResolve, setReconciliationItemToResolve] = useState<ReconciliationQueueItem | null>(null);
    const [resolutionQuery, setResolutionQuery] = useState<{ page: number; version: number } | null>(null);
    const [reconciliationEvidenceReference, setReconciliationEvidenceReference] = useState("");
    const [reconciliationRefundedAmount, setReconciliationRefundedAmount] = useState("");
    const [reconciliationEvidenceConfirmed, setReconciliationEvidenceConfirmed] = useState(false);
    const [reconciliationResolveError, setReconciliationResolveError] = useState<string | null>(null);
    const [reconciliationResolving, setReconciliationResolving] = useState(false);
    const reconciliationCurrent = confirmedReconciliationQuery?.page === reconciliationPage && confirmedReconciliationQuery.version === reconciliationRefreshVersion && !reconciliationLoading && !reconciliationError;
    const resolutionCurrent = reconciliationCurrent && resolutionQuery?.page === reconciliationPage && resolutionQuery.version === reconciliationRefreshVersion;
    const [workerHealth, setWorkerHealth] = useState<WorkerHealthSummary | null>(null);
    const [workerHealthLoading, setWorkerHealthLoading] = useState(false);
    const [workerHealthError, setWorkerHealthError] = useState(false);
    const [workerHealthRefreshVersion, setWorkerHealthRefreshVersion] = useState(0);
    const [legacyConnections, setLegacyConnections] = useState<LegacyMachineConnectionsSummary | null>(null);
    const [legacyConnectionsLoading, setLegacyConnectionsLoading] = useState(false);
    const [legacyConnectionsError, setLegacyConnectionsError] = useState(false);
    const [legacyConnectionsPage, setLegacyConnectionsPage] = useState(1);

    // Efeito para carregar logs
    useEffect(() => {
        if (!isAdmin) return;
        setLogsLoading(true);
        setLogsError(false);
        getAdminLogs(logsPage, 10, logsSearch)
            .then(data => {
                setLogsData(data);
                setLogsLoading(false);
            })
            .catch(err => {
                console.error("Erro ao carregar logs administrativos:", err);
                setLogsError(true);
                setLogsLoading(false);
            });
    }, [logsPage, logsSearch, logsRefreshVersion, isAdmin]);

    const handleLogsSearchSubmit = (e: React.FormEvent) => {
        e.preventDefault();
        setLogsPage(1);
        setLogsSearch(logsSearchInput);
    };

    const handleViewLogDetails = (log: AdminLog) => {
        setSelectedLogForDetails(log);
        setIsLogDetailsOpen(true);
    };

    useEffect(() => {
        if (!isAdmin) return;
        let active = true;
        setInboxLoading(true);
        setInboxError(null);
        getDeviceMessageInbox(inboxPage, 20)
            .then(data => {
                if (!active) return;
                setInboxData(data);
                setConfirmedInboxQuery({ page: inboxPage, version: inboxRefreshVersion });
                setInboxPage(page => Math.min(page, Math.max(1, data.totalPages)));
            })
            .catch(error => {
                if (!active) return;
                console.error("Erro ao consultar a inbox de telemetria:", error);
                setInboxError(error instanceof Error ? error.message : "Falha ao carregar a fila de telemetria.");
            })
            .finally(() => { if (active) setInboxLoading(false); });
        return () => { active = false; };
    }, [inboxPage, inboxRefreshVersion, isAdmin]);

    useEffect(() => {
        if (!isAdmin) return;
        let active = true;
        setReconciliationLoading(true);
        setReconciliationError(null);
        getReconciliationQueue(reconciliationPage, 20)
            .then(data => {
                if (!active) return;
                setReconciliationData(data);
                setConfirmedReconciliationQuery({ page: reconciliationPage, version: reconciliationRefreshVersion });
                setReconciliationPage(page => Math.min(page, Math.max(1, data.totalPages)));
            })
            .catch(error => {
                if (!active) return;
                console.error("Erro ao consultar a fila de conciliação financeira:", error);
                setReconciliationError(error instanceof Error ? error.message : "Falha ao carregar a fila de conciliação.");
            })
            .finally(() => { if (active) setReconciliationLoading(false); });
        return () => { active = false; };
    }, [reconciliationPage, reconciliationRefreshVersion, isAdmin]);

    useEffect(() => {
        if (!isAdmin) return;
        let active = true;
        setPaymentWebhookLoading(true);
        setPaymentWebhookError(null);
        getPaymentWebhookInbox(paymentWebhookStatus, paymentWebhookPage, 20)
            .then(data => {
                if (!active) return;
                setPaymentWebhookData(data);
                setConfirmedWebhookQuery({ page: paymentWebhookPage, status: paymentWebhookStatus, version: paymentWebhookRefreshVersion });
                setPaymentWebhookPage(page => Math.min(page, Math.max(1, data.totalPages)));
            })
            .catch(error => {
                console.error("Erro ao consultar a fila de webhooks:", error);
                if (active) setPaymentWebhookError(error instanceof Error ? error.message : "Falha ao carregar a fila de webhooks.");
            })
            .finally(() => {
                if (active) setPaymentWebhookLoading(false);
            });
        return () => {
            active = false;
        };
    }, [paymentWebhookPage, paymentWebhookRefreshVersion, paymentWebhookStatus, isAdmin]);

    useEffect(() => {
        if (!isAdmin) return;
        let cancelled = false;
        let refreshTimer: ReturnType<typeof setTimeout> | undefined;

        const refreshWorkerHealth = async () => {
            if (cancelled) return;
            setWorkerHealthLoading(true);
            setWorkerHealthError(false);
            try {
                const data = await getWorkerHealth();
                if (!cancelled) setWorkerHealth(data);
            } catch (error) {
                console.error("Erro ao consultar a saúde operacional:", error);
                if (!cancelled) setWorkerHealthError(true);
            } finally {
                if (!cancelled) {
                    setWorkerHealthLoading(false);
                    refreshTimer = setTimeout(refreshWorkerHealth, 60_000);
                }
            }
        };

        void refreshWorkerHealth();
        return () => {
            cancelled = true;
            if (refreshTimer !== undefined) clearTimeout(refreshTimer);
        };
    }, [workerHealthRefreshVersion, isAdmin]);

    useEffect(() => {
        if (!isAdmin) return;
        let cancelled = false;
        let refreshTimer: ReturnType<typeof setTimeout> | undefined;
        const refreshLegacyConnections = async () => {
            setLegacyConnectionsLoading(true);
            setLegacyConnectionsError(false);
            try {
                const data = await getLegacyMachineConnections(legacyConnectionsPage, 50);
                if (!cancelled) {
                    setLegacyConnections(data);
                    setLegacyConnectionsPage(page => Math.min(page, Math.max(1, data.totalPages)));
                }
            } catch (error) {
                console.error("Erro ao consultar máquinas conectadas à API anterior:", error);
                if (!cancelled) setLegacyConnectionsError(true);
            } finally {
                if (!cancelled) {
                    setLegacyConnectionsLoading(false);
                    refreshTimer = setTimeout(refreshLegacyConnections, 60_000);
                }
            }
        };
        void refreshLegacyConnections();
        return () => {
            cancelled = true;
            if (refreshTimer !== undefined) clearTimeout(refreshTimer);
        };
    }, [legacyConnectionsPage, workerHealthRefreshVersion, isAdmin]);

    const openReconciliationResolution = (item: ReconciliationQueueItem) => {
        if (!reconciliationCurrent || reconciliationResolving) return;
        setResolutionQuery({ page: reconciliationPage, version: reconciliationRefreshVersion });
        setReconciliationItemToResolve(item);
        setReconciliationEvidenceReference("");
        setReconciliationRefundedAmount("");
        setReconciliationEvidenceConfirmed(false);
        setReconciliationResolveError(null);
    };

    const handleResolvePaymentReconciliation = async (event: React.FormEvent<HTMLFormElement>) => {
        event.preventDefault();
        const item = reconciliationItemToResolve;
        const resolution = item ? getReconciliationResolution(item) : null;
        if (!item || !resolution || reconciliationResolving || !resolutionCurrent) return;

        const evidenceReference = reconciliationEvidenceReference.trim();
        if (!reconciliationEvidenceConfirmed) {
            setReconciliationResolveError("Confirme que consultou o comprovante ou chamado indicado antes de registrar a decisão.");
            return;
        }
        if (!/^[A-Za-z0-9._-]{3,64}$/.test(evidenceReference)) {
            setReconciliationResolveError("Informe o identificador do comprovante ou chamado (3 a 64 letras, números, ponto, hífen ou sublinhado). Não use links nem dados pessoais.");
            return;
        }

        let confirmedRefundedAmount: number | undefined;
        if (resolution.requiresRefundAmount) {
            confirmedRefundedAmount = Number(reconciliationRefundedAmount);
            const maximumRefundedAmount = resolution.resolution === "chargeback_reimbursed" ? item.amount : item.amount - 0.01;
            if (!Number.isFinite(confirmedRefundedAmount) || confirmedRefundedAmount < resolution.minimumRefundedAmount || confirmedRefundedAmount > maximumRefundedAmount) {
                setReconciliationResolveError("Informe o valor confirmado dentro do limite da transação.");
                return;
            }
        }

        setReconciliationResolving(true);
        setReconciliationResolveError(null);
        try {
            await resolvePaymentReconciliation(item.id, {
                resolution: resolution.resolution,
                evidenceReference,
                expectedProviderStatus: item.mercadoPagoStatus ?? "",
                expectedProviderStatusDetail: item.mercadoPagoStatusDetail ?? null,
                expectedReconciliationReason: item.reconciliationReason ?? null,
                expectedLastReconciliationAt: item.lastReconciliationAt ?? null,
                ...(confirmedRefundedAmount !== undefined ? { confirmedRefundedAmount } : {})
            });
            setReconciliationItemToResolve(null);
            setReconciliationRefreshVersion(version => version + 1);
        } catch (error) {
            console.error("Erro ao registrar a conciliação financeira:", error);
            setReconciliationResolveError("Não foi possível confirmar a decisão. Feche esta janela e confira a fila atualizada antes de tentar novamente.");
            setReconciliationRefreshVersion(version => version + 1);
        } finally {
            setReconciliationResolving(false);
        }
    };

    const handleRetryInboxMessage = async (id: string) => {
        if (!inboxCurrent || inboxRetryingId) return;
        if (!window.confirm("Reprocessar esta mensagem MDB pode repetir eventos operacionais. Deseja continuar?")) return;
        setInboxRetryingId(id);
        try {
            await retryDeviceMessageInbox(id);
            setInboxRefreshVersion(version => version + 1);
            window.alert("Mensagem reenfileirada para processamento.");
        } catch (error) {
            console.error("Erro ao reenfileirar mensagem de telemetria:", error);
            setInboxRefreshVersion(version => version + 1);
            window.alert("Não foi possível confirmar o reprocessamento. Confira a fila atualizada antes de tentar novamente.");
        } finally {
            setInboxRetryingId(null);
        }
    };

    const handleRetryPaymentWebhook = async (id: string) => {
        if (!webhookCurrent || paymentWebhookRetryingId) return;
        if (!window.confirm("A causa da falha deve estar corrigida. O reprocessamento consultará novamente o estado canônico do pagamento no Mercado Pago. Deseja continuar?")) return;
        setPaymentWebhookRetryingId(id);
        try {
            await retryPaymentWebhook(id);
            setPaymentWebhookRefreshVersion(version => version + 1);
            setWorkerHealthRefreshVersion(version => version + 1);
            window.alert("Webhook reenfileirado. Acompanhe o novo processamento nesta fila.");
        } catch (error) {
            console.error("Erro ao reenfileirar webhook:", error);
            setPaymentWebhookRefreshVersion(version => version + 1);
            setWorkerHealthRefreshVersion(version => version + 1);
            window.alert("Não foi possível confirmar o reprocessamento. Confira a fila atualizada antes de tentar novamente.");
        } finally {
            setPaymentWebhookRetryingId(null);
        }
    };

    const handleViewIntegrationDetails = async (companyId: string, companyName: string) => {
        const requestVersion = ++integrationRequestVersion.current;
        setSelectedCompanyForDetails({ id: companyId, name: companyName });
        setCompanyIntegration(null);
        setIntegrationDetailsError(false);
        setIsDetailsOpen(true);
        setLoadingDetails(true);
        try {
            const data = await getCompanyIntegration(companyId);
            if (requestVersion !== integrationRequestVersion.current) return;
            setCompanyIntegration(data);
        } catch (err) {
            if (requestVersion !== integrationRequestVersion.current) return;
            console.error("Erro ao carregar detalhes da integração da empresa:", err);
            setIntegrationDetailsError(true);
        } finally {
            if (requestVersion === integrationRequestVersion.current) setLoadingDetails(false);
        }
    };

    const handleDetailsOpenChange = (open: boolean) => {
        if (!open) {
            integrationRequestVersion.current += 1;
            setCompanyIntegration(null);
        }
        setIsDetailsOpen(open);
    };

    // Efeito para carregar as configurações de pagamento globais
    useEffect(() => {
        if (!isAdmin) return;
        let active = true;
        setSettingsLoading(true);
        setSettingsError(false);
        setSettingsErrorMessage("");
        getGlobalSettings()
            .then(data => {
                if (!active) return;
                setGlobalSettings(data);
                setFeeInput(data.applicationFeePercent);
                setConfirmedSettingsVersion(settingsRefreshVersion);
                setSettingsLoading(false);
            })
            .catch(err => {
                if (!active) return;
                console.error("Erro ao carregar configurações de pagamento:", err);
                setSettingsError(true);
                setSettingsErrorMessage("Não foi possível carregar as configurações de pagamento.");
                setSettingsLoading(false);
            });
        return () => { active = false; };
    }, [isAdmin, settingsRefreshVersion]);

    const handleSaveGlobalSettings = async () => {
        if (!settingsCurrent || savingSettings) return;
        const scaledFee = feeInput * 100;
        if (!Number.isFinite(feeInput) || feeInput <= 0 || feeInput >= 100 ||
            Math.abs(scaledFee - Math.round(scaledFee)) > 1e-8) {
            alert("A taxa da plataforma deve estar entre 0,01% e 99,99%, com no máximo duas casas decimais.");
            return;
        }

        setSavingSettings(true);
        try {
            await updateGlobalSettings({
                applicationFeePercent: feeInput
            }, globalSettings ?? undefined);
            setSettingsRefreshVersion(version => version + 1);
            alert("Configurações de pagamento atualizadas com sucesso!");
        } catch (err) {
            console.error("Erro ao salvar taxa global:", err);
            setSettingsRefreshVersion(version => version + 1);
            alert("Não foi possível confirmar a alteração. Confira a taxa atualizada antes de tentar novamente.");
        } finally {
            setSavingSettings(false);
        }
    };

    // Efeito para carregar usuários
    useEffect(() => {
        if (!isAdmin) return;
        let active = true;
        setUsersLoading(true);
        setUsersError(false);
        getUsers(userPage, 8, userSearch)
            .then(data => {
                if (!active) return;
                setUserData(data);
                setConfirmedUsersQuery({ page: userPage, search: userSearch, version: usersRefreshVersion });
                setUsersLoading(false);
            })
            .catch(() => {
                if (!active) return;
                setUsersError(true);
                setUsersLoading(false);
            });
        return () => { active = false; };
    }, [userPage, userSearch, usersRefreshVersion, isAdmin]);

    // Efeito para carregar empresas
    useEffect(() => {
        if (!isAdmin) return;
        let active = true;
        setCompaniesLoading(true);
        setCompaniesError(false);
        getCompanies(companyPage, 8, companySearch)
            .then(data => {
                if (!active) return;
                setCompanyData(data);
                setConfirmedCompaniesQuery({ page: companyPage, search: companySearch, version: companiesRefreshVersion });
                setCompaniesLoading(false);
            })
            .catch(() => {
                if (!active) return;
                setCompaniesError(true);
                setCompaniesLoading(false);
            });
        return () => { active = false; };
    }, [companyPage, companySearch, companiesRefreshVersion, isAdmin]);

    const handleUserSearchSubmit = (e: React.FormEvent) => {
        e.preventDefault();
        setUserPage(1);
        setUserSearch(userSearchInput);
    };

    const handleUserRoleChange = async (userId: string, role: "Admin" | "PlatformAdmin" | "Operator") => {
        if (!usersCurrent || roleChangeUserId !== null) return;
        if (!window.confirm("Alterar o papel deste usuário encerrará as sessões ativas dele. Continuar?")) return;
        setRoleChangeUserId(userId);
        try {
            await updateUserRole(userId, role);
            setUsersRefreshVersion(version => version + 1);
            alert("Papel atualizado. O usuário deverá entrar novamente.");
        } catch {
            setUsersRefreshVersion(version => version + 1);
            alert("Não foi possível confirmar a alteração. Confira a lista atualizada antes de tentar novamente.");
        } finally {
            setRoleChangeUserId(null);
        }
    };

    const handleCompanySearchSubmit = (e: React.FormEvent) => {
        e.preventDefault();
        setCompanyPage(1);
        setCompanySearch(companySearchInput);
    };

    const handleCompanyActivation = async (companyId: string, status: "Active" | "Rejected") => {
        if (!companiesCurrent || companyActivationId !== null) return;
        const isRejecting = status === "Rejected";
        const confirmation = isRejecting
            ? "Recusar esta empresa desativará os usuários vinculados e encerrará as sessões deles. Continuar?"
            : "Aprovar esta empresa liberará o acesso do administrador cadastrado. Como o envio automático de e-mail ainda não está configurado, será necessário avisar o contato cadastrado. Continuar?";
        if (!window.confirm(confirmation)) return;

        setCompanyActivationId(companyId);
        try {
            await updateCompanyActivation(companyId, status);
            setCompaniesRefreshVersion(version => version + 1);
            window.alert(isRejecting
                ? "Empresa recusada. Os usuários vinculados foram desativados."
                : "Empresa aprovada. O administrador já pode entrar; avise o contato cadastrado fora da plataforma.");
        } catch {
            setCompaniesRefreshVersion(version => version + 1);
            window.alert("Não foi possível confirmar a alteração. Confira a lista atualizada antes de tentar novamente.");
        } finally {
            setCompanyActivationId(null);
        }
    };

    if (!isAdmin) {
        return (
            <div className="flex flex-col items-center justify-center min-h-screen bg-slate-50 font-sans text-slate-900 p-6">
                <Card className="max-w-md w-full border-red-200 bg-white shadow-xl">
                    <CardHeader className="flex flex-col items-center pb-2">
                        <div className="w-12 h-12 bg-red-100 rounded-full flex items-center justify-center text-red-600 mb-2">
                            <ShieldAlert className="w-6 h-6" />
                        </div>
                        <CardTitle className="text-xl font-bold text-center text-red-600">Acesso Negado</CardTitle>
                        <CardDescription className="text-center">
                            Você não possui permissões administrativas para acessar esta página.
                        </CardDescription>
                    </CardHeader>
                    <CardContent className="flex justify-center pt-4 border-t gap-3">
                        <Button variant="outline" onClick={() => navigate("/dashboard")}>
                            Voltar para o Painel
                        </Button>
                        <Button variant="destructive" onClick={logout}>
                            Desconectar
                        </Button>
                    </CardContent>
                </Card>
            </div>
        );
    }

    return (
        <SidebarProvider>
            <div className="min-h-screen flex w-full">
                <AppSidebar />
                <div className="flex-1 flex flex-col min-w-0 bg-slate-50/50">
                    <header className="h-12 flex items-center border-b bg-white px-4 gap-3">
                        <SidebarTrigger />
                        <h1 className="text-sm font-semibold text-foreground">Administração</h1>
                        <div className="ml-auto flex items-center gap-4">
                            <span className="text-xs text-muted-foreground">
                                Perfil: <strong className="text-blue-600 font-semibold">{getRoleLabel(user?.role)}</strong>
                            </span>
                            <button
                                onClick={logout}
                                className="text-muted-foreground hover:text-foreground transition-colors flex items-center gap-2"
                                title="Sair"
                            >
                                <LogOut className="w-4 h-4" />
                            </button>
                        </div>
                    </header>

                    <main className="flex-1 p-6 space-y-6 overflow-auto">
                        <div className="pb-4 border-b">
                            <h2 className="text-2xl font-bold tracking-tight">Painel de Controle Administrativo</h2>
                            <p className="text-muted-foreground">
                                Gerencie usuários, empresas associadas e permissões do sistema.
                            </p>
                        </div>

                        <Tabs defaultValue={initialTab} className="w-full">
                            <TabsList className="bg-slate-100 p-1 rounded-lg border border-slate-200 inline-flex flex-wrap mb-4">
                                <TabsTrigger value="users" className="px-4 py-1.5 text-sm font-medium transition-all rounded-md data-[state=active]:bg-white data-[state=active]:shadow-xs">
                                    Usuários
                                </TabsTrigger>
                                <TabsTrigger value="companies" className="px-4 py-1.5 text-sm font-medium transition-all rounded-md data-[state=active]:bg-white data-[state=active]:shadow-xs">
                                    Empresas
                                </TabsTrigger>
                                <TabsTrigger value="payments" className="px-4 py-1.5 text-sm font-medium transition-all rounded-md data-[state=active]:bg-white data-[state=active]:shadow-xs">
                                    Pagamentos
                                </TabsTrigger>
                                <TabsTrigger value="payment-webhooks" className="px-4 py-1.5 text-sm font-medium transition-all rounded-md data-[state=active]:bg-white data-[state=active]:shadow-xs">
                                    Webhooks MP
                                </TabsTrigger>
                                <TabsTrigger value="logs" className="px-4 py-1.5 text-sm font-medium transition-all rounded-md data-[state=active]:bg-white data-[state=active]:shadow-xs">
                                    Logs de Transações
                                </TabsTrigger>
                                <TabsTrigger value="telemetry-inbox" className="px-4 py-1.5 text-sm font-medium transition-all rounded-md data-[state=active]:bg-white data-[state=active]:shadow-xs">
                                    Fila de Telemetria
                                </TabsTrigger>
                                <TabsTrigger value="operational-health" className="px-4 py-1.5 text-sm font-medium transition-all rounded-md data-[state=active]:bg-white data-[state=active]:shadow-xs">
                                    Saúde operacional
                                </TabsTrigger>
                            </TabsList>

                            {/* ABA DE USUÁRIOS */}
                            <TabsContent value="users" className="space-y-4 outline-hidden">
                                <Card className="border border-slate-200 bg-white shadow-xs">
                                    <CardHeader className="pb-4 border-b border-slate-100 flex flex-col md:flex-row md:items-center justify-between gap-4">
                                        <div>
                                            <CardTitle className="text-lg font-bold flex items-center gap-2">
                                                <UsersIcon className="w-5 h-5 text-blue-600" />
                                                Gerenciamento de Usuários
                                            </CardTitle>
                                            <CardDescription>
                                                Lista de todos os operadores e administradores com suas respectivas empresas.
                                            </CardDescription>
                                        </div>

                                        {/* Barra de Pesquisa Usuários */}
                                        <form onSubmit={handleUserSearchSubmit} className="flex items-center gap-2 max-w-sm w-full">
                                            <div className="relative w-full">
                                                <Search className="absolute left-2.5 top-2.5 h-4 w-4 text-muted-foreground" />
                                                <Input
                                                    type="search"
                                                    placeholder="Pesquisar por nome ou e-mail..."
                                                    aria-label="Pesquisar usuários"
                                                    className="pl-8 h-9 text-sm w-full bg-slate-50 border-slate-200 focus:bg-white"
                                                    value={userSearchInput}
                                                    onChange={(e) => setUserSearchInput(e.target.value)}
                                                />
                                            </div>
                                            <Button type="submit" size="sm" className="h-9 px-3">
                                                Buscar
                                            </Button>
                                        </form>
                                    </CardHeader>

                                    <CardContent className="p-0">
                                        {usersError && (
                                            <div role="alert" className="flex items-center justify-between gap-3 border-b border-red-200 bg-red-50 p-4 text-sm text-red-800">
                                                <span>{userData ? "Não foi possível atualizar os usuários. Os dados exibidos podem estar desatualizados." : "Não foi possível carregar os usuários."}</span>
                                                <Button variant="outline" size="sm" onClick={() => setUsersRefreshVersion(version => version + 1)} disabled={usersLoading}>Tentar novamente</Button>
                                            </div>
                                        )}
                                        <Table>
                                            <TableHeader className="bg-slate-50/70">
                                                <TableRow>
                                                    <TableHead className="px-6 font-semibold text-slate-700">Nome</TableHead>
                                                    <TableHead className="px-6 font-semibold text-slate-700">E-mail</TableHead>
                                                    <TableHead className="px-6 font-semibold text-slate-700">CPF</TableHead>
                                                    <TableHead className="px-6 font-semibold text-slate-700">Empresa</TableHead>
                                                    <TableHead className="px-6 font-semibold text-slate-700">Perfil</TableHead>
                                                    <TableHead className="px-6 font-semibold text-slate-700">Cadastrado em</TableHead>
                                                </TableRow>
                                            </TableHeader>
                                            <TableBody>
                                                {usersLoading && !userData ? (
                                                    Array.from({ length: 5 }).map((_, idx) => (
                                                        <TableRow key={idx}>
                                                            <TableCell className="px-6 py-4"><Skeleton className="h-4 w-32" /></TableCell>
                                                            <TableCell className="px-6 py-4"><Skeleton className="h-4 w-48" /></TableCell>
                                                            <TableCell className="px-6 py-4"><Skeleton className="h-4 w-28" /></TableCell>
                                                            <TableCell className="px-6 py-4"><Skeleton className="h-4 w-36" /></TableCell>
                                                            <TableCell className="px-6 py-4"><Skeleton className="h-5 w-16 rounded-full" /></TableCell>
                                                            <TableCell className="px-6 py-4"><Skeleton className="h-4 w-24" /></TableCell>
                                                        </TableRow>
                                                    ))
                                                ) : usersError && !userData ? (
                                                    <TableRow><TableCell colSpan={6} className="text-center py-12 text-red-700">Dados de usuários indisponíveis.</TableCell></TableRow>
                                                ) : !userData || userData.items.length === 0 ? (
                                                    <TableRow>
                                                        <TableCell colSpan={6} className="text-center py-12 text-slate-400 font-medium font-sans">
                                                            Nenhum usuário localizado.
                                                         </TableCell>
                                                    </TableRow>
                                                ) : (
                                                    userData.items.map((usr) => (
                                                        <TableRow key={usr.id} className="hover:bg-slate-50/50 transition-colors">
                                                            <TableCell className="px-6 py-3.5 font-medium text-slate-900">{usr.name}</TableCell>
                                                            <TableCell className="px-6 py-3.5 font-mono text-xs text-slate-600">{usr.email}</TableCell>
                                                            <TableCell className="px-6 py-3.5 font-mono text-xs text-slate-600">{usr.cpf || "-"}</TableCell>
                                                            <TableCell className="px-6 py-3.5 text-sm text-slate-600 font-medium">
                                                                {usr.companyName ? (
                                                                    <span className="text-slate-800">{usr.companyName}</span>
                                                                ) : (
                                                                    <span className="text-slate-400 italic font-normal">Nenhuma (Admin Geral)</span>
                                                                )}
                                                            </TableCell>
                                                            <TableCell className="px-6 py-3.5">
                                                                <select
                                                                    aria-label={`Papel de ${usr.name}`}
                                                                    value={usr.role}
                                                                    disabled={usr.id === user?.id || !usersCurrent || roleChangeUserId !== null}
                                                                    onChange={event => handleUserRoleChange(
                                                                        usr.id,
                                                                        event.target.value as "Admin" | "PlatformAdmin" | "Operator"
                                                                    )}
                                                                    className={`rounded-full px-2.5 py-1 text-xs font-semibold border ${
                                                                        usr.role === "PlatformAdmin"
                                                                            ? "bg-purple-50 text-purple-700 border-purple-200"
                                                                            : usr.role === "Admin"
                                                                                ? "bg-blue-50 text-blue-700 border-blue-200"
                                                                                : "bg-slate-100 text-slate-700 border-slate-200"
                                                                    }`}
                                                                >
                                                                    <option value="Operator">Operador</option>
                                                                    <option value="Admin">Administrador</option>
                                                                    <option value="PlatformAdmin">Administrador da plataforma</option>
                                                                </select>
                                                            </TableCell>
                                                            <TableCell className="px-6 py-3.5 text-xs text-slate-500 font-mono">
                                                                {new Date(usr.createdAt).toLocaleDateString("pt-BR", {
                                                                    day: "2-digit",
                                                                    month: "2-digit",
                                                                    year: "numeric",
                                                                    hour: "2-digit",
                                                                    minute: "2-digit"
                                                                })}
                                                            </TableCell>
                                                        </TableRow>
                                                    ))
                                                )}
                                            </TableBody>
                                        </Table>

                                        {/* Paginação Usuários */}
                                        {userData && userData.totalPages > 1 && (
                                            <div className="flex items-center justify-between px-6 py-4 border-t border-slate-100 bg-slate-50/30">
                                                <p className="text-xs text-slate-500 font-sans font-medium">
                                                    Mostrando {userData.items.length} de {userData.totalItems} usuários
                                                </p>
                                                <div className="flex items-center gap-1.5">
                                                    <Button
                                                        variant="outline"
                                                        size="sm"
                                                        className="h-8 px-2 border-slate-200"
                                                        onClick={() => setUserPage(p => Math.max(1, p - 1))}
                                                        disabled={userPage === 1 || !usersCurrent || roleChangeUserId !== null}
                                                    >
                                                        <ChevronLeft className="w-4 h-4 mr-1" />
                                                        Anterior
                                                    </Button>
                                                    <span className="text-xs font-sans font-semibold px-3 text-slate-700">
                                                        Página {userPage} de {userData.totalPages}
                                                    </span>
                                                    <Button
                                                        variant="outline"
                                                        size="sm"
                                                        className="h-8 px-2 border-slate-200"
                                                        onClick={() => setUserPage(p => Math.min(userData.totalPages, p + 1))}
                                                        disabled={userPage === userData.totalPages || !usersCurrent || roleChangeUserId !== null}
                                                    >
                                                        Próximo
                                                        <ChevronRight className="w-4 h-4 ml-1" />
                                                    </Button>
                                                </div>
                                            </div>
                                        )}
                                    </CardContent>
                                </Card>
                            </TabsContent>

                            {/* ABA DE EMPRESAS */}
                            <TabsContent value="companies" className="space-y-4 outline-hidden">
                                <Card className="border border-slate-200 bg-white shadow-xs">
                                    <CardHeader className="pb-4 border-b border-slate-100 flex flex-col md:flex-row md:items-center justify-between gap-4">
                                        <div>
                                            <CardTitle className="text-lg font-bold flex items-center gap-2">
                                                <Building2 className="w-5 h-5 text-emerald-600" />
                                                Gerenciamento de Empresas
                                            </CardTitle>
                                            <CardDescription>
                                                Lista de empresas parceiras, criadores da conta e seus respectivos sócios vinculados.
                                            </CardDescription>
                                        </div>

                                        {/* Barra de Pesquisa Empresas */}
                                        <form onSubmit={handleCompanySearchSubmit} className="flex items-center gap-2 max-w-sm w-full">
                                            <div className="relative w-full">
                                                <Search className="absolute left-2.5 top-2.5 h-4 w-4 text-muted-foreground" />
                                                <Input
                                                    type="search"
                                                    placeholder="Pesquisar por nome da empresa..."
                                                    aria-label="Pesquisar empresas"
                                                    className="pl-8 h-9 text-sm w-full bg-slate-50 border-slate-200 focus:bg-white"
                                                    value={companySearchInput}
                                                    onChange={(e) => setCompanySearchInput(e.target.value)}
                                                />
                                            </div>
                                            <Button type="submit" size="sm" className="h-9 px-3">
                                                Buscar
                                            </Button>
                                        </form>
                                    </CardHeader>

                                    <CardContent className="p-0">
                                        {companiesError && (
                                            <div role="alert" className="flex items-center justify-between gap-3 border-b border-red-200 bg-red-50 p-4 text-sm text-red-800">
                                                <span>{companyData ? "Não foi possível atualizar as empresas. Os dados exibidos podem estar desatualizados." : "Não foi possível carregar as empresas."}</span>
                                                <Button variant="outline" size="sm" onClick={() => setCompaniesRefreshVersion(version => version + 1)} disabled={companiesLoading}>Tentar novamente</Button>
                                            </div>
                                        )}
                                        <Table>
                                            <TableHeader className="bg-slate-50/70">
                                                <TableRow>
                                                    <TableHead className="px-6 font-semibold text-slate-700">Nome da Empresa</TableHead>
                                                    <TableHead className="px-6 font-semibold text-slate-700">CNPJ</TableHead>
                                                    <TableHead className="px-6 font-semibold text-slate-700">Dono / Criador</TableHead>
                                                    <TableHead className="px-6 font-semibold text-slate-700">Sócios Vinculados</TableHead>
                                                    <TableHead className="px-6 font-semibold text-slate-700">Data de Criação</TableHead>
                                                    <TableHead className="px-6 font-semibold text-slate-700">Ativação</TableHead>
                                                    <TableHead className="px-6 font-semibold text-slate-700">Integrações</TableHead>
                                                    <TableHead className="px-6 font-semibold text-slate-700 text-right">Ações</TableHead>
                                                </TableRow>
                                            </TableHeader>
                                            <TableBody>
                                                {companiesLoading && !companyData ? (
                                                    Array.from({ length: 3 }).map((_, idx) => (
                                                        <TableRow key={idx}>
                                                            <TableCell className="px-6 py-4"><Skeleton className="h-4 w-40" /></TableCell>
                                                            <TableCell className="px-6 py-4"><Skeleton className="h-4 w-32" /></TableCell>
                                                            <TableCell className="px-6 py-4"><Skeleton className="h-4 w-32" /></TableCell>
                                                            <TableCell className="px-6 py-4"><Skeleton className="h-4 w-48" /></TableCell>
                                                            <TableCell className="px-6 py-4"><Skeleton className="h-4 w-24" /></TableCell>
                                                            <TableCell className="px-6 py-4"><Skeleton className="h-5 w-20" /></TableCell>
                                                            <TableCell className="px-6 py-4"><Skeleton className="h-4 w-24" /></TableCell>
                                                            <TableCell className="px-6 py-4 text-right"><Skeleton className="h-8 w-16 ml-auto" /></TableCell>
                                                        </TableRow>
                                                    ))
                                                ) : companiesError && !companyData ? (
                                                    <TableRow><TableCell colSpan={8} className="text-center py-12 text-red-700">Dados de empresas indisponíveis.</TableCell></TableRow>
                                                ) : !companyData || companyData.items.length === 0 ? (
                                                    <TableRow>
                                                        <TableCell colSpan={8} className="text-center py-12 text-slate-400 font-medium font-sans">
                                                            Nenhuma empresa localizada.
                                                        </TableCell>
                                                    </TableRow>
                                                ) : (
                                                    companyData.items.map((comp) => (
                                                        <TableRow key={comp.id} className="hover:bg-slate-50/50 transition-colors">
                                                            <TableCell className="px-6 py-3.5 font-bold text-slate-900">{comp.name}</TableCell>
                                                            <TableCell className="px-6 py-3.5 font-mono text-xs text-slate-600">{comp.cnpj || "-"}</TableCell>
                                                            <TableCell className="px-6 py-3.5 font-semibold text-blue-600">
                                                                <div>{comp.createdBy}</div>
                                                                <div className="text-xs font-normal text-slate-500">{comp.createdByEmail}</div>
                                                            </TableCell>
                                                            <TableCell className="px-6 py-3.5">
                                                                <div className="flex flex-wrap gap-1.5 max-w-md">
                                                                    {comp.partners.length > 0 ? (
                                                                        comp.partners.map((partner, pidx) => (
                                                                            <span key={pidx} className="inline-flex items-center px-2 py-0.5 rounded bg-slate-100 text-slate-700 border border-slate-200 text-xs font-medium">
                                                                                {partner}
                                                                            </span>
                                                                        ))
                                                                    ) : (
                                                                        <span className="text-slate-400 italic text-xs">Sem sócios cadastrados</span>
                                                                    )}
                                                                </div>
                                                            </TableCell>
                                                            <TableCell className="px-6 py-3.5 text-xs text-slate-500 font-mono">
                                                                {new Date(comp.createdAt).toLocaleDateString("pt-BR", {
                                                                    day: "2-digit",
                                                                    month: "2-digit",
                                                                    year: "numeric"
                                                                })}
                                                            </TableCell>
                                                            <TableCell className="px-6 py-3.5">
                                                                <Badge variant={comp.activationStatus === "Active" ? "default" : comp.activationStatus === "Pending" ? "secondary" : "destructive"}>
                                                                    {comp.activationStatus === "Active" ? "Ativa" : comp.activationStatus === "Pending" ? "Pendente" : "Recusada"}
                                                                </Badge>
                                                            </TableCell>
                                                            <TableCell className="px-6 py-3.5">
                                                                <div className="flex flex-wrap gap-1">
                                                                    {comp.enabledIntegrations && comp.enabledIntegrations.length > 0 ? (
                                                                        comp.enabledIntegrations.map((integrationName, idx) => (
                                                                            <span key={idx} className="inline-flex items-center px-2 py-0.5 rounded-full bg-emerald-50 text-emerald-700 border border-emerald-200 text-xs font-semibold uppercase">
                                                                                {integrationName}
                                                                            </span>
                                                                        ))
                                                                    ) : (
                                                                        <span className="text-slate-400 italic text-xs">Nenhuma</span>
                                                                    )}
                                                                </div>
                                                            </TableCell>
                                                            <TableCell className="px-6 py-3.5 text-right">
                                                                <div className="flex flex-wrap justify-end gap-2">
                                                                    {comp.activationStatus !== "Active" && (
                                                                        <Button
                                                                            size="sm"
                                                                            disabled={!companiesCurrent || companyActivationId !== null}
                                                                            onClick={() => handleCompanyActivation(comp.id, "Active")}
                                                                            className="h-8 text-xs font-semibold"
                                                                        >
                                                                            {comp.activationStatus === "Pending" ? "Aprovar" : "Reativar"}
                                                                        </Button>
                                                                    )}
                                                                    {comp.activationStatus === "Pending" && (
                                                                        <Button
                                                                            variant="destructive"
                                                                            size="sm"
                                                                            disabled={!companiesCurrent || companyActivationId !== null}
                                                                            onClick={() => handleCompanyActivation(comp.id, "Rejected")}
                                                                            className="h-8 text-xs font-semibold"
                                                                        >
                                                                            Recusar
                                                                        </Button>
                                                                    )}
                                                                    <Button
                                                                        variant="outline"
                                                                        size="sm"
                                                                        disabled={!companiesCurrent || companyActivationId !== null}
                                                                        onClick={() => handleViewIntegrationDetails(comp.id, comp.name)}
                                                                        className="h-8 text-xs font-semibold"
                                                                    >
                                                                        Detalhes
                                                                    </Button>
                                                                </div>
                                                            </TableCell>
                                                        </TableRow>
                                                    ))
                                                )}
                                            </TableBody>
                                        </Table>

                                        {/* Paginação Empresas */}
                                        {companyData && companyData.totalPages > 1 && (
                                            <div className="flex items-center justify-between px-6 py-4 border-t border-slate-100 bg-slate-50/30">
                                                <p className="text-xs text-slate-500 font-sans font-medium">
                                                    Mostrando {companyData.items.length} de {companyData.totalItems} empresas
                                                </p>
                                                <div className="flex items-center gap-1.5">
                                                    <Button
                                                        variant="outline"
                                                        size="sm"
                                                        className="h-8 px-2 border-slate-200"
                                                        onClick={() => setCompanyPage(p => Math.max(1, p - 1))}
                                                        disabled={companyPage === 1 || !companiesCurrent || companyActivationId !== null}
                                                    >
                                                        <ChevronLeft className="w-4 h-4 mr-1" />
                                                        Anterior
                                                    </Button>
                                                    <span className="text-xs font-sans font-semibold px-3 text-slate-700">
                                                        Página {companyPage} de {companyData.totalPages}
                                                    </span>
                                                    <Button
                                                        variant="outline"
                                                        size="sm"
                                                        className="h-8 px-2 border-slate-200"
                                                        onClick={() => setCompanyPage(p => Math.min(companyData.totalPages, p + 1))}
                                                        disabled={companyPage === companyData.totalPages || !companiesCurrent || companyActivationId !== null}
                                                    >
                                                        Próximo
                                                        <ChevronRight className="w-4 h-4 ml-1" />
                                                    </Button>
                                                </div>
                                            </div>
                                        )}
                                    </CardContent>
                                </Card>
                            </TabsContent>
                            
                            {/* ABA DE CONFIGURAÇÃO DE PAGAMENTO (Taxa Global) */}
                            <TabsContent value="payments" className="space-y-4 outline-hidden">
                                <Card className="border border-slate-200 bg-white shadow-xs max-w-2xl">
                                    <CardHeader className="pb-4 border-b border-slate-100">
                                        <CardTitle className="text-lg font-bold flex items-center gap-2">
                                            <CreditCard className="w-5 h-5 text-indigo-600" />
                                            Configurações de Taxa de Pagamento
                                        </CardTitle>
                                        <CardDescription>
                                            Defina a taxa global cobrada pela plataforma sobre as transações de Checkout Transparente.
                                        </CardDescription>
                                    </CardHeader>
                                    <CardContent className="p-6 space-y-6">
                                        {settingsError && (
                                            <div role="alert" className="flex items-center justify-between gap-3 rounded-md border border-red-200 bg-red-50 p-4 text-sm text-red-800">
                                                <span>{settingsErrorMessage || (globalSettings ? "Não foi possível atualizar as configurações. Os valores exibidos podem estar desatualizados." : "Não foi possível carregar as configurações de pagamento.")}</span>
                                                <Button variant="outline" size="sm" onClick={() => setSettingsRefreshVersion(version => version + 1)} disabled={settingsLoading}>Tentar novamente</Button>
                                            </div>
                                        )}
                                        {globalSettings && (globalSettings.isConfigured === false || globalSettings.isValid === false) && (
                                            <div role="alert" className="rounded-md border border-amber-300 bg-amber-50 p-4 text-sm text-amber-900">
                                                A taxa global não está configurada corretamente. Novas cobranças Pix estão bloqueadas até salvar uma taxa válida.
                                            </div>
                                        )}
                                        {settingsLoading && !globalSettings ? (
                                            <div className="space-y-2 py-4">
                                                <Skeleton className="h-4 w-1/3" />
                                                <Skeleton className="h-10 w-full" />
                                            </div>
                                        ) : !globalSettings ? (
                                            <p className="py-4 text-center text-sm text-muted-foreground">Configurações indisponíveis.</p>
                                        ) : (
                                            <div className="space-y-4">
                                                <div className="space-y-1.5">
                                                    <label htmlFor="platform-fee" className="text-sm font-semibold text-slate-700">
                                                        Taxa de Comissão do Site (%)
                                                    </label>
                                            <p className="text-xs text-muted-foreground">
                                                        A taxa deve ficar entre 0,01% e 99,99%. O valor da comissão é arredondado para centavos; se resultar em menos de R$ 0,01 ou em valor igual/superior ao pagamento, a cobrança será recusada antes da criação do Pix.
                                                    </p>
                                                    <div className="flex items-center gap-3 mt-2">
                                                        <div className="relative max-w-[150px] w-full">
                                                            <Input
                                                                id="platform-fee"
                                                                type="number"
                                                                step="0.01"
                                                                min="0.01"
                                                                max="99.99"
                                                                className="pr-8 text-lg font-mono-data text-right focus:border-indigo-500"
                                                                value={feeInput}
                                                                disabled={!settingsCurrent || savingSettings}
                                                                onChange={(e) => setFeeInput(parseFloat(e.target.value) || 0)}
                                                            />
                                                            <span className="absolute right-3 top-2.5 text-slate-400 font-semibold text-sm">%</span>
                                                        </div>
                                                    </div>
                                                </div>

                                                <div className="bg-indigo-50 border border-indigo-100 rounded-lg p-4 flex gap-3 text-indigo-800 text-sm mt-4 select-none">
                                                    <Activity className="w-5 h-5 shrink-0 mt-0.5" />
                                                    <div>
                                                        <strong className="font-semibold block mb-0.5">Exemplo Prático:</strong>
                                                        Se a taxa for configurada em <strong className="font-mono-data">{feeInput}%</strong>, uma venda de <strong className="font-mono-data">R$ 10,00</strong> tentará gerar uma comissão de <strong className="font-mono-data">R$ {(10 * (feeInput / 100)).toFixed(2)}</strong> para o site, e a empresa integrada receberá o valor restante líquido quando o split for aceito.
                                                    </div>
                                                </div>

                                                <div className="flex justify-end pt-2">
                                                    <Button
                                                        type="button"
                                                        onClick={handleSaveGlobalSettings}
                                                        disabled={savingSettings || !settingsCurrent}
                                                        className="gap-2 bg-indigo-600 hover:bg-indigo-700 font-semibold"
                                                    >
                                                        <Save className="w-4 h-4" />
                                                        {savingSettings ? "Salvando..." : "Salvar taxa"}
                                                    </Button>
                                                </div>
                                            </div>
                                        )}
                                    </CardContent>
                                </Card>
                                <Card className="border border-slate-200 bg-white shadow-xs">
                                    <CardHeader className="pb-4 border-b border-slate-100 flex flex-col md:flex-row md:items-center justify-between gap-3">
                                        <div>
                                            <CardTitle className="text-lg font-bold flex items-center gap-2">
                                                <Activity className="w-5 h-5 text-amber-600" />
                                                Conciliação financeira pendente
                                            </CardTitle>
                                            <CardDescription>
                                                Cobranças ainda pendentes ou que precisam de verificação. A consulta não altera o estado dos pagamentos.
                                            </CardDescription>
                                        </div>
                                        <Button variant="outline" size="sm" onClick={() => setReconciliationRefreshVersion(version => version + 1)} disabled={reconciliationLoading} className="gap-2">
                                            <RefreshCw className={`w-4 h-4 ${reconciliationLoading ? "animate-spin" : ""}`} />
                                            Atualizar
                                        </Button>
                                    </CardHeader>
                                    <CardContent className="p-0">
                                        <div className="grid grid-cols-2 gap-3 p-4 border-b bg-slate-50/50">
                                            <div className="rounded-md border bg-white p-3">
                                                <p className="text-xs text-muted-foreground">Pendentes</p>
                                                <p className="text-xl font-semibold">{reconciliationData?.counts.pending ?? (reconciliationError || reconciliationLoading ? "—" : 0)}</p>
                                            </div>
                                            <div className="rounded-md border bg-white p-3">
                                                <p className="text-xs text-muted-foreground">Exigem verificação</p>
                                                <p className="text-xl font-semibold">{reconciliationData?.counts.reconciliationRequired ?? (reconciliationError || reconciliationLoading ? "—" : 0)}</p>
                                            </div>
                                        </div>
                                        {reconciliationError && (
                                            <p role="alert" className="border-b p-4 text-sm text-destructive">
                                                {reconciliationData
                                                    ? "Não foi possível atualizar a conciliação. Os dados exibidos podem estar desatualizados."
                                                    : "Não foi possível carregar a fila de conciliação. Use Atualizar para tentar novamente."}
                                            </p>
                                        )}
                                        <div className="overflow-x-auto">
                                                <Table>
                                                    <TableHeader>
                                                        <TableRow>
                                                            <TableHead>Criação</TableHead>
                                                            <TableHead>Empresa / máquina</TableHead>
                                                            <TableHead>Valor</TableHead>
                                                            <TableHead>Estado</TableHead>
                                                            <TableHead>Estado no provedor</TableHead>
                                                            <TableHead>Última consulta</TableHead>
                                                            <TableHead>Ação</TableHead>
                                                        </TableRow>
                                                    </TableHeader>
                                                    <TableBody>
                                                        {reconciliationLoading && !reconciliationData ? (
                                                            <TableRow><TableCell colSpan={7} className="p-6 text-center text-muted-foreground">Carregando conciliação...</TableCell></TableRow>
                                                        ) : reconciliationError && !reconciliationData ? (
                                                            <TableRow><TableCell colSpan={7} className="p-6 text-center text-destructive">Dados de conciliação indisponíveis.</TableCell></TableRow>
                                                        ) : !reconciliationData || reconciliationData.items.length === 0 ? (
                                                            <TableRow><TableCell colSpan={7} className="p-6 text-center text-muted-foreground">Nenhum pagamento pendente de conciliação.</TableCell></TableRow>
                                                        ) : reconciliationData.items.map(item => (
                                                            <TableRow key={item.id}>
                                                                <TableCell className="whitespace-nowrap text-xs">{new Date(item.createdAt).toLocaleString("pt-BR")}</TableCell>
                                                                <TableCell><span className="block font-medium">{item.companyName}</span><span className="text-xs text-muted-foreground">{item.machineName} · {item.machineId}</span></TableCell>
                                                                <TableCell className="whitespace-nowrap">{new Intl.NumberFormat("pt-BR", { style: "currency", currency: "BRL" }).format(item.amount)}</TableCell>
                                                                <TableCell><Badge variant={item.status === "ReconciliationRequired" ? "destructive" : "secondary"}>{item.status === "ReconciliationRequired" ? "Verificação necessária" : "Pendente"}</Badge></TableCell>
                                                                <TableCell><span>{item.mercadoPagoStatus || "Sem status"}</span>{item.mercadoPagoStatusDetail && <span className="block text-xs text-muted-foreground">{item.mercadoPagoStatusDetail}</span>}{item.reconciliationReason && <span className="block text-xs text-destructive">{reconciliationReasonLabels[item.reconciliationReason] ?? "Verificação financeira necessária"}</span>}</TableCell>
                                                                <TableCell className="whitespace-nowrap text-xs">{item.lastReconciliationAt ? new Date(item.lastReconciliationAt).toLocaleString("pt-BR") : "Ainda não consultado"}</TableCell>
                                                                <TableCell className="whitespace-nowrap">
                                                                    {item.status === "ReconciliationRequired" && getReconciliationResolution(item) ? (
                                                                        <Button type="button" variant="outline" size="sm" onClick={() => openReconciliationResolution(item)} disabled={reconciliationResolving || !reconciliationCurrent}>
                                                                            Registrar conferência
                                                                        </Button>
                                                                    ) : item.status === "ReconciliationRequired" ? (
                                                                        <span className="text-xs text-muted-foreground">Aguardando estado compatível</span>
                                                                    ) : "—"}
                                                                </TableCell>
                                                            </TableRow>
                                                        ))}
                                                    </TableBody>
                                                </Table>
                                        </div>
                                        {reconciliationData && reconciliationData.totalPages > 1 && (
                                            <div className="flex items-center justify-between border-t p-3">
                                                <span className="text-xs text-muted-foreground">{reconciliationData.totalItems} pagamentos · página {reconciliationPage} de {reconciliationData.totalPages}</span>
                                                <div className="flex gap-2">
                                                    <Button variant="outline" size="sm" onClick={() => setReconciliationPage(page => Math.max(1, page - 1))} disabled={reconciliationPage <= 1 || !reconciliationCurrent}>Anterior</Button>
                                                    <Button variant="outline" size="sm" onClick={() => setReconciliationPage(page => Math.min(reconciliationData.totalPages, page + 1))} disabled={reconciliationPage >= reconciliationData.totalPages || !reconciliationCurrent}>Próxima</Button>
                                                </div>
                                            </div>
                                        )}
                                    </CardContent>
                                </Card>
                            </TabsContent>

                            <TabsContent value="payment-webhooks" className="space-y-4 outline-hidden">
                                <Card className="border border-slate-200 bg-white shadow-xs">
                                    <CardHeader className="pb-4 border-b border-slate-100 flex flex-col md:flex-row md:items-center md:justify-between gap-4">
                                        <div>
                                            <CardTitle className="text-lg font-bold flex items-center gap-2">
                                                <Activity className="w-5 h-5 text-amber-600" />
                                                Inbox de webhooks do Mercado Pago
                                            </CardTitle>
                                            <CardDescription>
                                                Consulte o estado, tentativas e erro sanitizado. O conteúdo original do webhook não é exibido.
                                            </CardDescription>
                                        </div>
                                        <Button variant="outline" size="sm" onClick={() => setPaymentWebhookRefreshVersion(version => version + 1)} disabled={paymentWebhookLoading}>
                                            <RefreshCw className={`w-4 h-4 mr-2 ${paymentWebhookLoading ? "animate-spin" : ""}`} />
                                            Atualizar
                                        </Button>
                                    </CardHeader>
                                    <CardContent className="p-6 space-y-5">
                                        <div className="flex flex-col sm:flex-row sm:items-end sm:justify-between gap-3">
                                            <div className="space-y-1.5">
                                                <label htmlFor="payment-webhook-status" className="text-sm font-medium">Estado do webhook</label>
                                                <select
                                                    id="payment-webhook-status"
                                                    className="h-10 min-w-56 rounded-md border border-input bg-background px-3 py-2 text-sm"
                                                    value={paymentWebhookStatus}
                                                    onChange={event => {
                                                        setPaymentWebhookStatus(event.target.value as PaymentWebhookStatus | "All");
                                                        setPaymentWebhookPage(1);
                                                        setPaymentWebhookData(null);
                                                    }}
                                                >
                                                    <option value="Failed">Falha final</option>
                                                    <option value="Pending">Pendente</option>
                                                    <option value="Retry">Nova tentativa</option>
                                                    <option value="Processing">Processando</option>
                                                    <option value="Processed">Processado</option>
                                                    <option value="All">Todos os estados</option>
                                                </select>
                                            </div>
                                            <p className="text-xs text-muted-foreground">
                                                {paymentWebhookData?.totalItems ?? (paymentWebhookError || paymentWebhookLoading ? "—" : 0)} evento(s) neste filtro.
                                            </p>
                                        </div>
                                        {paymentWebhookError && (
                                            <p role="alert" className="rounded-md border border-red-200 bg-red-50 p-3 text-sm text-red-800">
                                                {paymentWebhookData ? "Não foi possível atualizar a fila. Os dados exibidos podem estar desatualizados." : paymentWebhookError}
                                            </p>
                                        )}
                                        <div className="overflow-x-auto rounded-lg border border-slate-200">
                                            <Table>
                                                <TableHeader className="bg-slate-50/70">
                                                    <TableRow>
                                                        <TableHead>Recebido em</TableHead>
                                                        <TableHead>Pagamento</TableHead>
                                                        <TableHead>Evento</TableHead>
                                                        <TableHead>Origem / autenticação</TableHead>
                                                        <TableHead>Estado</TableHead>
                                                        <TableHead>Tentativas</TableHead>
                                                        <TableHead>Próxima tentativa</TableHead>
                                                        <TableHead>Erro sanitizado</TableHead>
                                                        <TableHead className="text-right">Ação</TableHead>
                                                    </TableRow>
                                                </TableHeader>
                                                <TableBody>
                                                    {paymentWebhookLoading && !paymentWebhookData ? (
                                                        <TableRow><TableCell colSpan={9} className="py-10 text-center text-muted-foreground">Carregando webhooks...</TableCell></TableRow>
                                                    ) : paymentWebhookError && !paymentWebhookData ? (
                                                        <TableRow><TableCell colSpan={9} className="py-10 text-center text-destructive">Fila de webhooks indisponível.</TableCell></TableRow>
                                                    ) : !paymentWebhookData || paymentWebhookData.items.length === 0 ? (
                                                        <TableRow><TableCell colSpan={9} className="py-10 text-center text-muted-foreground">Nenhum webhook neste estado.</TableCell></TableRow>
                                                    ) : paymentWebhookData.items.map(item => (
                                                        <TableRow key={item.id}>
                                                            <TableCell className="whitespace-nowrap text-xs">{new Date(item.receivedAt).toLocaleString("pt-BR")}</TableCell>
                                                            <TableCell className="max-w-40 break-all font-mono text-xs">{item.paymentId}</TableCell>
                                                            <TableCell className="text-xs"><span className="block">{item.eventType}</span><span className="text-muted-foreground">{item.eventAction}</span></TableCell>
                                                            <TableCell className="text-xs"><span className="block">{item.format}</span><span className="text-muted-foreground">{item.authenticationMode}</span></TableCell>
                                                            <TableCell><Badge variant={item.status === "Failed" ? "destructive" : item.status === "Processed" ? "secondary" : "outline"}>{paymentWebhookStatusLabels[item.status]}</Badge></TableCell>
                                                            <TableCell>{item.attempts}</TableCell>
                                                            <TableCell className="whitespace-nowrap text-xs">{item.nextAttemptAt ? new Date(item.nextAttemptAt).toLocaleString("pt-BR") : "—"}</TableCell>
                                                            <TableCell className="max-w-56 whitespace-normal text-xs">{item.lastError || "—"}</TableCell>
                                                            <TableCell className="text-right">
                                                                {item.status === "Failed" ? (
                                                                    <Button size="sm" variant="outline" onClick={() => void handleRetryPaymentWebhook(item.id)} disabled={!webhookCurrent || paymentWebhookRetryingId !== null}>
                                                                        <RotateCw className="w-3.5 h-3.5 mr-1.5" />
                                                                        Reprocessar
                                                                    </Button>
                                                                ) : "—"}
                                                            </TableCell>
                                                        </TableRow>
                                                    ))}
                                                </TableBody>
                                            </Table>
                                        </div>
                                        {paymentWebhookData && paymentWebhookData.totalPages > 1 && (
                                            <div className="flex items-center justify-between">
                                                <p className="text-xs text-muted-foreground">Página {paymentWebhookPage} de {paymentWebhookData.totalPages}</p>
                                                <div className="flex items-center gap-2">
                                                    <Button variant="outline" size="sm" onClick={() => setPaymentWebhookPage(page => Math.max(1, page - 1))} disabled={paymentWebhookPage <= 1 || !webhookCurrent}>Anterior</Button>
                                                    <Button variant="outline" size="sm" onClick={() => setPaymentWebhookPage(page => Math.min(paymentWebhookData.totalPages, page + 1))} disabled={paymentWebhookPage >= paymentWebhookData.totalPages || !webhookCurrent}>Próxima</Button>
                                                </div>
                                            </div>
                                        )}
                                    </CardContent>
                                </Card>
                            </TabsContent>

                            {/* ABA DE LOGS DE TRANSAÇÕES */}
                            <TabsContent value="logs" className="space-y-4 outline-hidden">
                                <Card className="border border-slate-200 bg-white shadow-xs">
                                    <CardHeader className="pb-4 border-b border-slate-100 flex flex-col md:flex-row md:items-center justify-between gap-4">
                                        <div>
                                            <CardTitle className="text-lg font-bold flex items-center gap-2">
                                                <Terminal className="w-5 h-5 text-indigo-600" />
                                                Logs e Diagnóstico de API
                                            </CardTitle>
                                            <CardDescription>
                                                Histórico de telemetria de transações e erros retornados pelo Mercado Pago em todo o sistema.
                                            </CardDescription>
                                        </div>

                                        {/* Barra de Pesquisa de Logs */}
                                        <form onSubmit={handleLogsSearchSubmit} className="flex items-center gap-2 max-w-sm w-full">
                                            <div className="relative w-full">
                                                <Search className="absolute left-2.5 top-2.5 h-4 w-4 text-muted-foreground" />
                                                <Input
                                                    type="search"
                                                    placeholder="Buscar por erro, máquina, empresa..."
                                                    className="pl-8 h-9 text-sm w-full bg-slate-50 border-slate-200 focus:bg-white"
                                                    value={logsSearchInput}
                                                    onChange={(e) => setLogsSearchInput(e.target.value)}
                                                />
                                            </div>
                                            <Button type="submit" size="sm" className="h-9 px-3">
                                                Buscar
                                            </Button>
                                        </form>
                                    </CardHeader>

                                    <CardContent className="p-0">
                                        {logsError && (
                                            <div role="alert" className="flex items-center justify-between gap-3 border-b border-red-200 bg-red-50 p-4 text-sm text-red-800">
                                                <span>{logsData ? "Não foi possível atualizar os logs. Os dados exibidos podem estar desatualizados." : "Não foi possível carregar os logs administrativos."}</span>
                                                <Button variant="outline" size="sm" onClick={() => setLogsRefreshVersion(version => version + 1)} disabled={logsLoading}>Tentar novamente</Button>
                                            </div>
                                        )}
                                        <Table>
                                            <TableHeader className="bg-slate-50/70">
                                                <TableRow>
                                                    <TableHead className="px-6 font-semibold text-slate-700">Data / Hora</TableHead>
                                                    <TableHead className="px-6 font-semibold text-slate-700">Empresa</TableHead>
                                                    <TableHead className="px-6 font-semibold text-slate-700">Máquina</TableHead>
                                                    <TableHead className="px-6 font-semibold text-slate-700">Tipo</TableHead>
                                                    <TableHead className="px-6 font-semibold text-slate-700">Mensagem</TableHead>
                                                    <TableHead className="px-6 font-semibold text-slate-700 text-right">Ação</TableHead>
                                                </TableRow>
                                            </TableHeader>
                                            <TableBody>
                                                {logsLoading && !logsData ? (
                                                    Array.from({ length: 5 }).map((_, idx) => (
                                                        <TableRow key={idx}>
                                                            <TableCell className="px-6 py-4"><Skeleton className="h-4 w-28" /></TableCell>
                                                            <TableCell className="px-6 py-4"><Skeleton className="h-4 w-32" /></TableCell>
                                                            <TableCell className="px-6 py-4"><Skeleton className="h-4 w-32" /></TableCell>
                                                            <TableCell className="px-6 py-4"><Skeleton className="h-5 w-14 rounded-full" /></TableCell>
                                                            <TableCell className="px-6 py-4"><Skeleton className="h-4 w-64" /></TableCell>
                                                            <TableCell className="px-6 py-4 text-right"><Skeleton className="h-8 w-16 ml-auto" /></TableCell>
                                                        </TableRow>
                                                    ))
                                                ) : logsError && !logsData ? (
                                                    <TableRow><TableCell colSpan={6} className="text-center py-12 text-red-700">Dados de logs indisponíveis.</TableCell></TableRow>
                                                ) : !logsData || logsData.items.length === 0 ? (
                                                    <TableRow>
                                                        <TableCell colSpan={6} className="text-center py-12 text-slate-400 font-medium font-sans">
                                                            Nenhum log registrado na base de dados.
                                                        </TableCell>
                                                    </TableRow>
                                                ) : (
                                                    logsData.items.map((log) => (
                                                        <TableRow key={log.id} className="hover:bg-slate-50/50 transition-colors">
                                                            <TableCell className="px-6 py-3.5 text-xs text-slate-500 font-mono">
                                                                {new Date(log.timestamp).toLocaleDateString("pt-BR", {
                                                                    day: "2-digit",
                                                                    month: "2-digit",
                                                                    year: "numeric",
                                                                    hour: "2-digit",
                                                                    minute: "2-digit",
                                                                    second: "2-digit"
                                                                })}
                                                            </TableCell>
                                                            <TableCell className="px-6 py-3.5 text-sm text-slate-800 font-semibold">{log.companyName}</TableCell>
                                                            <TableCell className="px-6 py-3.5 text-sm text-slate-600 font-medium">{log.machineName}</TableCell>
                                                            <TableCell className="px-6 py-3.5">
                                                                <Badge variant={log.logType === "Error" ? "destructive" : "secondary"} className={`text-[10px] font-semibold px-2 py-0.5 ${
                                                                    log.logType === "Error" 
                                                                        ? "bg-red-50 text-red-700 border-red-200" 
                                                                        : "bg-blue-50 text-blue-700 border-blue-200"
                                                                }`}>
                                                                    {log.logType}
                                                                </Badge>
                                                            </TableCell>
                                                            <TableCell className="px-6 py-3.5 text-sm text-slate-600 font-sans max-w-xs truncate" title={log.message}>
                                                                {log.message}
                                                            </TableCell>
                                                            <TableCell className="px-6 py-3.5 text-right">
                                                                <Button
                                                                    variant="outline"
                                                                    size="sm"
                                                                    onClick={() => handleViewLogDetails(log)}
                                                                    className="h-8 text-xs font-semibold"
                                                                >
                                                                    Ver Log
                                                                </Button>
                                                            </TableCell>
                                                        </TableRow>
                                                    ))
                                                )}
                                            </TableBody>
                                        </Table>

                                        {/* Paginação de Logs */}
                                        {logsData && logsData.totalPages > 1 && (
                                            <div className="flex items-center justify-between px-6 py-4 border-t border-slate-100 bg-slate-50/30">
                                                <p className="text-xs text-slate-500 font-sans font-medium">
                                                    Mostrando {logsData.items.length} de {logsData.totalItems} registros de log
                                                </p>
                                                <div className="flex items-center gap-1.5">
                                                    <Button
                                                        variant="outline"
                                                        size="sm"
                                                        className="h-8 px-2 border-slate-200"
                                                        onClick={() => setLogsPage(p => Math.max(1, p - 1))}
                                                        disabled={logsPage === 1 || logsLoading}
                                                    >
                                                        <ChevronLeft className="w-4 h-4 mr-1" />
                                                        Anterior
                                                    </Button>
                                                    <span className="text-xs font-sans font-semibold px-3 text-slate-700">
                                                        Página {logsPage} de {logsData.totalPages}
                                                    </span>
                                                    <Button
                                                        variant="outline"
                                                        size="sm"
                                                        className="h-8 px-2 border-slate-200"
                                                        onClick={() => setLogsPage(p => Math.min(logsData.totalPages, p + 1))}
                                                        disabled={logsPage === logsData.totalPages || logsLoading}
                                                    >
                                                        Próximo
                                                        <ChevronRight className="w-4 h-4 ml-1" />
                                                    </Button>
                                                </div>
                                            </div>
                                        )}
                                    </CardContent>
                                </Card>
                            </TabsContent>

                            <TabsContent value="telemetry-inbox" className="space-y-4 outline-hidden">
                                <Card className="border border-slate-200 bg-white shadow-xs">
                                    <CardHeader className="pb-4 border-b border-slate-100 flex flex-col md:flex-row md:items-center md:justify-between gap-4">
                                        <div>
                                            <CardTitle className="text-lg font-bold flex items-center gap-2">
                                                <Activity className="w-5 h-5 text-amber-600" />
                                                Inbox durável de telemetria
                                            </CardTitle>
                                            <CardDescription>
                                                Eventos MDB são guardados antes do ACK. Falhas permanecem disponíveis para reprocessamento auditado.
                                            </CardDescription>
                                        </div>
                                        <Button variant="outline" size="sm" onClick={() => setInboxRefreshVersion(version => version + 1)} disabled={inboxLoading}>
                                            <RefreshCw className={`w-4 h-4 mr-2 ${inboxLoading ? "animate-spin" : ""}`} />
                                            Atualizar
                                        </Button>
                                    </CardHeader>
                                    <CardContent className="p-6 space-y-5">
                                        {inboxError && <p role="alert" className="rounded-md border border-red-200 bg-red-50 p-3 text-sm text-red-800">{inboxData ? "Não foi possível atualizar a fila. Os dados exibidos podem estar desatualizados." : "Não foi possível carregar a fila de telemetria. Use Atualizar para tentar novamente."}</p>}
                                        <div className="grid grid-cols-2 md:grid-cols-5 gap-3">
                                            {[
                                                ["Pendentes", "Pending"],
                                                ["Em nova tentativa", "Retry"],
                                                ["Processando", "Processing"],
                                                ["Falhas", "Failed"],
                                                ["Processados", "Processed"]
                                            ].map(([label, key]) => (
                                                <div key={key} className="rounded-lg border border-slate-200 bg-slate-50 p-3">
                                                    <p className="text-xs text-muted-foreground">{label}</p>
                                                    <p className="text-xl font-semibold">{inboxData?.counts[key] ?? (inboxError || inboxLoading ? "—" : 0)}</p>
                                                </div>
                                            ))}
                                        </div>

                                        <div className="overflow-x-auto rounded-lg border border-slate-200">
                                            <Table>
                                                <TableHeader className="bg-slate-50/70">
                                                    <TableRow>
                                                        <TableHead className="px-4">Recebida em</TableHead>
                                                        <TableHead className="px-4">Máquina</TableHead>
                                                        <TableHead className="px-4">Serial</TableHead>
                                                        <TableHead className="px-4">Mensagem MDB</TableHead>
                                                        <TableHead className="px-4">Tentativas</TableHead>
                                                        <TableHead className="px-4">Erro</TableHead>
                                                        <TableHead className="px-4 text-right">Ação</TableHead>
                                                    </TableRow>
                                                </TableHeader>
                                                <TableBody>
                                                    {inboxLoading && !inboxData ? (
                                                        <TableRow><TableCell colSpan={7} className="py-10 text-center text-muted-foreground">Carregando fila...</TableCell></TableRow>
                                                    ) : inboxError && !inboxData ? (
                                                        <TableRow><TableCell colSpan={7} className="py-10 text-center text-red-700">Não foi possível carregar as falhas.</TableCell></TableRow>
                                                    ) : !inboxData || inboxData.failures.length === 0 ? (
                                                        <TableRow><TableCell colSpan={7} className="py-10 text-center text-muted-foreground">Nenhuma mensagem em falha.</TableCell></TableRow>
                                                    ) : inboxData.failures.map(item => (
                                                        <TableRow key={item.id}>
                                                            <TableCell className="px-4 whitespace-nowrap text-xs">{new Date(item.receivedAt).toLocaleString("pt-BR")}</TableCell>
                                                            <TableCell className="px-4 font-mono text-xs">{item.machineId}</TableCell>
                                                            <TableCell className="px-4 font-mono text-xs">{item.serial}</TableCell>
                                                            <TableCell className="px-4">#{item.messageId}</TableCell>
                                                            <TableCell className="px-4">{item.attempts}</TableCell>
                                                            <TableCell className="px-4 text-xs">{item.lastError || "Falha sem detalhe"}</TableCell>
                                                            <TableCell className="px-4 text-right">
                                                                <Button size="sm" variant="outline" onClick={() => void handleRetryInboxMessage(item.id)} disabled={!inboxCurrent || inboxRetryingId !== null}>
                                                                    <RotateCw className="w-3.5 h-3.5 mr-1.5" />
                                                                    Reprocessar
                                                                </Button>
                                                            </TableCell>
                                                        </TableRow>
                                                    ))}
                                                </TableBody>
                                            </Table>
                                        </div>

                                        {inboxData && inboxData.totalPages > 1 && (
                                            <div className="flex items-center justify-between">
                                                <p className="text-xs text-muted-foreground">{inboxData.totalFailed} mensagens em falha</p>
                                                <div className="flex items-center gap-2">
                                                    <Button variant="outline" size="sm" onClick={() => setInboxPage(page => Math.max(1, page - 1))} disabled={inboxPage <= 1 || !inboxCurrent}>Anterior</Button>
                                                    <span className="text-xs">Página {inboxPage} de {inboxData.totalPages}</span>
                                                    <Button variant="outline" size="sm" onClick={() => setInboxPage(page => Math.min(inboxData.totalPages, page + 1))} disabled={inboxPage >= inboxData.totalPages || !inboxCurrent}>Próxima</Button>
                                                </div>
                                            </div>
                                        )}
                                    </CardContent>
                                </Card>
                            </TabsContent>

                            <TabsContent value="operational-health" className="space-y-4 outline-hidden">
                                <Card className="border border-slate-200 bg-white shadow-xs">
                                    <CardHeader className="pb-4 border-b border-slate-100 flex flex-col md:flex-row md:items-center md:justify-between gap-4">
                                        <div>
                                            <CardTitle className="text-lg font-bold flex items-center gap-2">
                                                <Activity className="w-5 h-5 text-blue-600" />
                                                Saúde de workers e filas
                                            </CardTitle>
                                            <CardDescription>
                                                Diagnóstico da API e do banco, atualizado a cada minuto. Esta leitura não altera workers, filas nem máquinas.
                                            </CardDescription>
                                        </div>
                                        <Button variant="outline" size="sm" onClick={() => setWorkerHealthRefreshVersion(version => version + 1)} disabled={workerHealthLoading}>
                                            <RefreshCw className={`w-4 h-4 mr-2 ${workerHealthLoading ? "animate-spin" : ""}`} />
                                            Atualizar
                                        </Button>
                                    </CardHeader>
                                    <CardContent className="p-6 space-y-5">
                                        {workerHealthError && (
                                            <p role="alert" className="rounded-md border border-red-200 bg-red-50 p-3 text-sm text-red-800">
                                                {workerHealth
                                                    ? "Não foi possível atualizar a saúde operacional. Os dados exibidos podem estar desatualizados."
                                                    : "Não foi possível carregar a saúde operacional. Use Atualizar para tentar novamente."}
                                            </p>
                                        )}
                                        {workerHealthLoading && !workerHealth ? (
                                            <p className="text-sm text-muted-foreground">Carregando saúde operacional...</p>
                                        ) : !workerHealth && !workerHealthError ? (
                                            <p className="text-sm text-muted-foreground">Aguardando leitura operacional.</p>
                                        ) : workerHealth && (
                                            <>
                                                <p className="text-xs text-muted-foreground">
                                                    Leitura gerada em {new Date(workerHealth.generatedAt).toLocaleString("pt-BR")}.
                                                </p>
                                                <div className="rounded-lg border bg-slate-50 p-3">
                                                    <p className="text-xs text-muted-foreground">Commit desta versão</p>
                                                    <p className="font-mono text-sm break-all">{workerHealth.sourceCommit ?? "Não registrado nesta imagem"}</p>
                                                </div>
                                                {workerHealth.hasUnhealthyWorkers && (
                                                    <p role="status" className="rounded-md border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900">
                                                        Há workers não iniciados, degradados, parados ou sem atualização. Confira o estado de cada um abaixo.
                                                    </p>
                                                )}
                                                {workerHealth.queueMetrics.available && (workerHealth.queueMetrics.webhookInbox?.Failed ?? 0) > 0 && (
                                                    <p role="status" className="rounded-md border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900">
                                                        Há {workerHealth.queueMetrics.webhookInbox?.Failed} webhook(s) em falha terminal. Revise e reprocese a fila de webhooks antes de liberar uma nova implantação.
                                                    </p>
                                                )}
                                                {workerHealth.queueMetrics.available && (workerHealth.queueMetrics.deviceMessageInbox?.Failed ?? 0) > 0 && (
                                                    <p role="status" className="rounded-md border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900">
                                                        Há {workerHealth.queueMetrics.deviceMessageInbox?.Failed} mensagem(ns) MDB em falha terminal. Revise e reprocese a inbox antes de liberar uma nova implantação.
                                                    </p>
                                                )}
                                                {workerHealth.queueMetrics.available && (workerHealth.queueMetrics.financialOutbox?.failed ?? 0) > 0 && (
                                                    <p role="status" className="rounded-md border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900">
                                                        Há {workerHealth.queueMetrics.financialOutbox?.failed} operações financeiras com falha terminal. Confira a conciliação antes de uma nova implantação.
                                                    </p>
                                                )}
                                                {workerHealth.queueMetrics.available && (workerHealth.queueMetrics.financialOutbox?.deferred_legacy_overlap ?? 0) > 0 && (
                                                    <p role="status" className="rounded-md border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900">
                                                        Há {workerHealth.queueMetrics.financialOutbox?.deferred_legacy_overlap} operação(ões) financeira(s) adiadas durante a sobreposição. Após retirar a API antiga e remover a configuração do relay, acompanhe a fila até a retomada e conclusão.
                                                    </p>
                                                )}
                                                {workerHealth.queueMetrics.available &&
                                                    ((workerHealth.queueMetrics.localTelemetryCommands?.pendingIntents ?? 0) > 0 ||
                                                     (workerHealth.queueMetrics.localTelemetryCommands?.unresolvedResults ?? 0) > 0) && (
                                                        <p role="status" className="rounded-md border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900">
                                                            Há {workerHealth.queueMetrics.localTelemetryCommands?.pendingIntents ?? 0} intenção(ões) de comando manual em andamento e {workerHealth.queueMetrics.localTelemetryCommands?.unresolvedResults ?? 0} resultado(s) incerto(s) sem conciliação. Aguarde envios ativos e confira fisicamente os incertos antes de liberar uma nova implantação.
                                                        </p>
                                                    )}
                                                {workerHealth.queueMetrics.available && workerHealth.queueMetrics.machineConnections &&
                                                    (workerHealth.queueMetrics.machineConnections.staleOnline > 0 ||
                                                     (workerHealth.queueMetrics.machineConnections.readinessGate?.unroutable ?? 0) > 0 ||
                                                     ((workerHealth.queueMetrics.machineConnections.legacyRelay?.eligibleConnections ?? 0) > 0 &&
                                                      workerHealth.queueMetrics.machineConnections.legacyRelay?.healthy !== true)) && (
                                                        <p role="status" className="rounded-md border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900">
                                                            Há máquinas conectadas sem atualização recente ou conexões sem rota MDB segura. Confira o estado da telemetria antes de qualquer implantação.
                                                        </p>
                                                    )}
                                                {workerHealth.queueMetrics.available &&
                                                    (workerHealth.queueMetrics.machineConnections?.legacyRelay?.eligibleConnections ?? 0) > 0 &&
                                                    workerHealth.queueMetrics.machineConnections?.legacyRelay?.healthy === true && (
                                                        <p role="status" className="rounded-md border border-sky-300 bg-sky-50 p-3 text-sm text-sky-950">
                                                            Há {workerHealth.queueMetrics.machineConnections.legacyRelay.eligibleConnections} conexão(ões) ainda pertencente(s) à API antiga. Mantenha esse processo ativo até as conexões migrarem e a contagem chegar a zero.
                                                        </p>
                                                    )}

                                                {(legacyConnectionsError || (legacyConnections?.totalItems ?? 0) > 0) && (
                                                    <div className="rounded-lg border p-4 space-y-3">
                                                        <div>
                                                            <h3 className="text-sm font-semibold">Máquinas ainda conectadas à API anterior</h3>
                                                            <p className="text-xs text-muted-foreground">Use esta lista para acompanhar a migração. Ela é somente leitura; mantenha a API anterior ativa enquanto houver conexões.</p>
                                                        </div>
                                                        {legacyConnectionsError && (
                                                            <p role="alert" className="text-sm text-amber-800">Não foi possível carregar a lista detalhada. A contagem geral continua disponível quando a saúde operacional é carregada.</p>
                                                        )}
                                                        {legacyConnectionsLoading && !legacyConnections && <p className="text-sm text-muted-foreground">Carregando conexões...</p>}
                                                        {legacyConnections && (
                                                            <>
                                                                <p className="text-xs text-muted-foreground">{legacyConnections.totalItems} conexão(ões); relay {legacyConnections.relay.healthy === true ? "saudável" : legacyConnections.relay.healthy === false ? "indisponível" : "sem conexões"}. Atualizado em {new Date(legacyConnections.generatedAt).toLocaleString("pt-BR")}.</p>
                                                                <div className="overflow-x-auto rounded-md border">
                                                                    <Table>
                                                                        <TableHeader><TableRow><TableHead>Empresa</TableHead><TableHead>Máquina</TableHead><TableHead>Número de série</TableHead><TableHead>Último contato</TableHead><TableHead>Sessão ativa</TableHead></TableRow></TableHeader>
                                                                        <TableBody>
                                                                            {legacyConnections.items.map(machine => (
                                                                                <TableRow key={machine.machineId}>
                                                                                    <TableCell>{machine.companyName}</TableCell>
                                                                                    <TableCell>{machine.machineName}</TableCell>
                                                                                    <TableCell className="font-mono text-xs">{machine.serialNumber}</TableCell>
                                                                                    <TableCell>{new Date(machine.lastSeenAt).toLocaleString("pt-BR")}</TableCell>
                                                                                    <TableCell>{machine.activeSessionState ? `${machine.activeSessionState}${machine.activeSessionStartedAt ? ` · ${new Date(machine.activeSessionStartedAt).toLocaleString("pt-BR")}` : ""}` : "Nenhuma"}</TableCell>
                                                                                </TableRow>
                                                                            ))}
                                                                            {legacyConnections.items.length === 0 && <TableRow><TableCell colSpan={5} className="text-center text-muted-foreground">Nenhuma conexão nesta página.</TableCell></TableRow>}
                                                                        </TableBody>
                                                                    </Table>
                                                                </div>
                                                                {legacyConnections.totalPages > 1 && (
                                                                    <div className="flex items-center justify-end gap-2">
                                                                        <Button variant="outline" size="sm" onClick={() => setLegacyConnectionsPage(page => Math.max(1, page - 1))} disabled={legacyConnectionsPage <= 1 || legacyConnectionsLoading}><ChevronLeft className="mr-1 h-4 w-4" />Anterior</Button>
                                                                        <span className="text-xs">Página {legacyConnections.page} de {legacyConnections.totalPages}</span>
                                                                        <Button variant="outline" size="sm" onClick={() => setLegacyConnectionsPage(page => Math.min(legacyConnections.totalPages, page + 1))} disabled={legacyConnectionsPage >= legacyConnections.totalPages || legacyConnectionsLoading}>Próxima<ChevronRight className="ml-1 h-4 w-4" /></Button>
                                                                    </div>
                                                                )}
                                                            </>
                                                        )}
                                                    </div>
                                                )}

                                                {workerHealth.queueMetrics.available ? (
                                                    <>
                                                        <div className="grid grid-cols-2 lg:grid-cols-3 xl:grid-cols-6 gap-3">
                                                            <div className="rounded-lg border bg-slate-50 p-3"><p className="text-xs text-muted-foreground">Máquinas monitoradas</p><p className="text-xl font-semibold">{workerHealth.queueMetrics.machineConnections?.monitored ?? 0}</p></div>
                                                            <div className="rounded-lg border bg-slate-50 p-3"><p className="text-xs text-muted-foreground">Máquinas conectadas</p><p className="text-xl font-semibold">{workerHealth.queueMetrics.machineConnections?.online ?? 0}</p></div>
                                                            <div className="rounded-lg border bg-slate-50 p-3"><p className="text-xs text-muted-foreground">Conectadas sem atualização</p><p className="text-xl font-semibold">{workerHealth.queueMetrics.machineConnections?.staleOnline ?? 0}</p></div>
                                                            <div className="rounded-lg border bg-slate-50 p-3"><p className="text-xs text-muted-foreground">Conectadas sem proprietário</p><p className="text-xl font-semibold">{workerHealth.queueMetrics.machineConnections?.onlineWithoutOwner ?? 0}</p></div>
                                                            <div className="rounded-lg border bg-slate-50 p-3"><p className="text-xs text-muted-foreground">No processo anterior</p><p className="text-xl font-semibold">{workerHealth.queueMetrics.machineConnections?.legacyRelay?.eligibleConnections ?? 0}</p><p className="text-xs text-muted-foreground">Relay: {workerHealth.queueMetrics.machineConnections?.legacyRelay?.healthy === true ? "saudável" : workerHealth.queueMetrics.machineConnections?.legacyRelay?.eligibleConnections ? "indisponível" : "sem conexões legadas"}</p></div>
                                                            <div className="rounded-lg border bg-slate-50 p-3"><p className="text-xs text-muted-foreground">Pagamentos pendentes</p><p className="text-xl font-semibold">{workerHealth.queueMetrics.pendingPayments ?? 0}</p><p className="text-xs text-muted-foreground">Mais antigo: {formatAge(workerHealth.queueMetrics.oldestPendingPaymentAgeSeconds)}</p></div>
                                                        </div>
                                                        {workerHealth.queueMetrics.machineConnections?.readinessGate && (
                                                            <div className="rounded-lg border p-4 space-y-3">
                                                                <div>
                                                                    <h3 className="text-sm font-semibold">Prontidão das conexões para release</h3>
                                                                    <p className="text-xs text-muted-foreground">Contagens que bloqueiam a rota da release; sockets legados com relay saudável aparecem acima e devem migrar antes de retirar a API antiga.</p>
                                                                </div>
                                                                <div className="grid grid-cols-2 md:grid-cols-5 gap-3">
                                                                    <div className="rounded-lg border bg-slate-50 p-3"><p className="text-xs text-muted-foreground">No escopo do gate</p><p className="text-xl font-semibold">{workerHealth.queueMetrics.machineConnections.readinessGate.onlineInGateScope}</p></div>
                                                                    <div className="rounded-lg border bg-slate-50 p-3"><p className="text-xs text-muted-foreground">Sem rota segura</p><p className="text-xl font-semibold">{workerHealth.queueMetrics.machineConnections.readinessGate.unroutable}</p></div>
                                                                    <div className="rounded-lg border bg-slate-50 p-3"><p className="text-xs text-muted-foreground">Proprietário inconsistente</p><p className="text-xl font-semibold">{workerHealth.queueMetrics.machineConnections.readinessGate.withoutOwner}</p></div>
                                                                    <div className="rounded-lg border bg-slate-50 p-3"><p className="text-xs text-muted-foreground">Roteamento incompatível</p><p className="text-xl font-semibold">{workerHealth.queueMetrics.machineConnections.readinessGate.withoutCommandRouting}</p></div>
                                                                    <div className="rounded-lg border bg-slate-50 p-3"><p className="text-xs text-muted-foreground">Empresa divergente</p><p className="text-xl font-semibold">{workerHealth.queueMetrics.machineConnections.readinessGate.companyMismatch}</p></div>
                                                                </div>
                                                            </div>
                                                        )}
                                                        <div className="grid md:grid-cols-2 xl:grid-cols-4 gap-3">
                                                            {[
                                                                { title: "Inbox MDB", counts: workerHealth.queueMetrics.deviceMessageInbox, age: workerHealth.queueMetrics.oldestDeviceMessageAgeSeconds },
                                                                { title: "Inbox de webhooks", counts: workerHealth.queueMetrics.webhookInbox, age: workerHealth.queueMetrics.oldestWebhookAgeSeconds },
                                                                { title: "Comandos MDB", counts: workerHealth.queueMetrics.telemetryCommandDispatches, age: workerHealth.queueMetrics.oldestPendingTelemetryDispatchAgeSeconds },
                                                                { title: "Outbox financeira", counts: workerHealth.queueMetrics.financialOutbox, age: workerHealth.queueMetrics.oldestPendingFinancialOutboxAgeSeconds, deferredAge: workerHealth.queueMetrics.oldestDeferredLegacyFinancialOutboxAgeSeconds },
                                                            ].map(queue => (
                                                                <div key={queue.title} className="rounded-lg border p-4 space-y-2">
                                                                    <h3 className="text-sm font-semibold">{queue.title}</h3>
                                                                    <div className="grid grid-cols-2 gap-2 text-xs">
                                                                        <span>Pendentes: <strong>{queue.counts?.Pending ?? queue.counts?.pending ?? 0}</strong></span>
                                                                        <span>Em nova tentativa: <strong>{queue.counts?.Retry ?? queue.counts?.retry ?? 0}</strong></span>
                                                                        <span>Processando: <strong>{queue.counts?.Processing ?? queue.counts?.processing ?? 0}</strong></span>
                                                                        <span>Falhas: <strong>{queue.counts?.Failed ?? queue.counts?.failed ?? 0}</strong></span>
                                                                        {queue.title === "Outbox financeira" && (queue.counts?.deferred_legacy_overlap ?? 0) > 0 && (
                                                                            <span>Adiadas na sobreposição: <strong>{queue.counts.deferred_legacy_overlap}</strong></span>
                                                                        )}
                                                                    </div>
                                                                    <p className="text-xs text-muted-foreground">Item pendente mais antigo: {formatAge(queue.age)}</p>
                                                                    {queue.title === "Outbox financeira" && (queue.counts?.deferred_legacy_overlap ?? 0) > 0 && (
                                                                        <p className="text-xs text-muted-foreground">Operação adiada mais antiga: {formatAge(queue.deferredAge)}</p>
                                                                    )}
                                                                    {queue.title === "Comandos MDB" && <p className="text-xs text-muted-foreground">Processando há mais tempo: {formatAge(workerHealth.queueMetrics.oldestProcessingTelemetryDispatchAgeSeconds)}</p>}
                                                                    {queue.title === "Outbox financeira" && (queue.counts?.processing ?? 0) > 0 && <p className="text-xs text-muted-foreground">{formatLeaseExpiry(workerHealth.queueMetrics.earliestProcessingFinancialOutboxLeaseExpiryAt)}</p>}
                                                                </div>
                                                            ))}
                                                        </div>
                                                        <div className="grid md:grid-cols-2 xl:grid-cols-4 gap-3">
                                                            <div className="rounded-lg border p-4 space-y-2">
                                                                <h3 className="text-sm font-semibold">Comandos MDB manuais</h3>
                                                                <p className="text-xs">Intenções em andamento: <strong>{workerHealth.queueMetrics.localTelemetryCommands?.pendingIntents ?? 0}</strong></p>
                                                                <p className="text-xs">Intenção mais antiga: {formatAge(workerHealth.queueMetrics.localTelemetryCommands?.oldestPendingIntentAgeSeconds)}</p>
                                                                <p className="text-xs">Resultados incertos sem conciliação: <strong>{workerHealth.queueMetrics.localTelemetryCommands?.unresolvedResults ?? 0}</strong></p>
                                                                <p className="text-xs text-muted-foreground">Resultado incerto mais antigo: {formatAge(workerHealth.queueMetrics.localTelemetryCommands?.oldestUnresolvedResultAgeSeconds)}</p>
                                                            </div>
                                                        </div>
                                                    </>
                                                ) : (
                                                    <p role="alert" className="rounded-md border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900">
                                                        O banco não respondeu à consulta das filas. O estado local dos workers continua disponível abaixo.
                                                    </p>
                                                )}

                                                <div className="overflow-x-auto rounded-lg border border-slate-200">
                                                    <Table>
                                                        <TableHeader className="bg-slate-50/70">
                                                            <TableRow>
                                                                <TableHead>Worker</TableHead>
                                                                <TableHead>Estado</TableHead>
                                                                <TableHead>Último heartbeat</TableHead>
                                                                <TableHead>Último sucesso</TableHead>
                                                                <TableHead>Falhas consecutivas</TableHead>
                                                                <TableHead>Tipo da última falha</TableHead>
                                                            </TableRow>
                                                        </TableHeader>
                                                        <TableBody>
                                                            {workerHealth.workers.length === 0 ? (
                                                                <TableRow><TableCell colSpan={6} className="p-6 text-center text-muted-foreground">Nenhum worker registrado.</TableCell></TableRow>
                                                            ) : workerHealth.workers.map(worker => (
                                                                <TableRow key={worker.name}>
                                                                    <TableCell className="font-medium">{getWorkerDisplayName(worker.name)}</TableCell>
                                                                    <TableCell><Badge variant={worker.status === "Healthy" ? "default" : worker.status === "NotStarted" ? "outline" : "destructive"}>{getWorkerStatusLabel(worker.status)}</Badge></TableCell>
                                                                    <TableCell className="whitespace-nowrap text-xs">{worker.lastHeartbeatAt ? new Date(worker.lastHeartbeatAt).toLocaleString("pt-BR") : "—"}</TableCell>
                                                                    <TableCell className="whitespace-nowrap text-xs">{worker.lastSuccessAt ? new Date(worker.lastSuccessAt).toLocaleString("pt-BR") : "—"}</TableCell>
                                                                    <TableCell>{worker.consecutiveFailures}</TableCell>
                                                                    <TableCell className="font-mono text-xs">{worker.lastErrorType ?? "—"}</TableCell>
                                                                </TableRow>
                                                            ))}
                                                        </TableBody>
                                                    </Table>
                                                </div>
                                            </>
                                        )}
                                    </CardContent>
                                </Card>
                            </TabsContent>
                        </Tabs>

                        <Dialog
                            open={reconciliationItemToResolve !== null}
                            onOpenChange={open => {
                                if (!open && !reconciliationResolving) setReconciliationItemToResolve(null);
                            }}
                        >
                            <DialogContent className="max-w-lg">
                                <DialogHeader>
                                    <DialogTitle>Registrar conferência financeira</DialogTitle>
                                    <DialogDescription>
                                        Esta ação somente registra uma decisão já confirmada no Mercado Pago; não envia pagamento, cancelamento ou estorno ao provedor.
                                    </DialogDescription>
                                </DialogHeader>
                                {reconciliationItemToResolve && (() => {
                                    const resolution = getReconciliationResolution(reconciliationItemToResolve);
                                    if (!resolution) return null;
                                    return (
                                        <form onSubmit={handleResolvePaymentReconciliation} className="space-y-4">
                                            <div className="rounded-md border bg-muted/30 p-3 text-sm">
                                                <p className="font-medium">{resolution.label}</p>
                                                <p className="mt-1 text-muted-foreground">
                                                    {reconciliationItemToResolve.companyName} · {reconciliationItemToResolve.machineName}
                                                </p>
                                                <p className="text-muted-foreground">
                                                    Estado informado: {reconciliationItemToResolve.mercadoPagoStatus}
                                                    {reconciliationItemToResolve.mercadoPagoStatusDetail ? ` / ${reconciliationItemToResolve.mercadoPagoStatusDetail}` : ""}
                                                </p>
                                            </div>
                                            {resolution.requiresRefundAmount && (
                                                <div className="space-y-2">
                                                    <label htmlFor="confirmed-refunded-amount" className="text-sm font-medium">{resolution.refundAmountLabel}</label>
                                                    <Input
                                                        id="confirmed-refunded-amount"
                                                        type="number"
                                                        min={resolution.minimumRefundedAmount}
                                                        max={resolution.resolution === "chargeback_reimbursed" ? reconciliationItemToResolve.amount : Math.max(0, reconciliationItemToResolve.amount - 0.01)}
                                                        step="0.01"
                                                        required
                                                        value={reconciliationRefundedAmount}
                                                        onChange={event => setReconciliationRefundedAmount(event.target.value)}
                                                    />
                                                </div>
                                            )}
                                            <div className="space-y-2">
                                                <label htmlFor="reconciliation-evidence-reference" className="text-sm font-medium">Referência do comprovante ou chamado</label>
                                                <Input
                                                    id="reconciliation-evidence-reference"
                                                    required
                                                    minLength={3}
                                                    maxLength={64}
                                                    pattern="[A-Za-z0-9._-]{3,64}"
                                                    placeholder="Ex.: MP-CASE-2048"
                                                    value={reconciliationEvidenceReference}
                                                    onChange={event => setReconciliationEvidenceReference(event.target.value)}
                                                />
                                                <p className="text-xs text-muted-foreground">Use somente o identificador; não informe links, dados pessoais ou credenciais.</p>
                                            </div>
                                            <label className="flex items-start gap-2 text-sm">
                                                <input
                                                    type="checkbox"
                                                    required
                                                    checked={reconciliationEvidenceConfirmed}
                                                    onChange={event => setReconciliationEvidenceConfirmed(event.target.checked)}
                                                    className="mt-1"
                                                />
                                                <span>Consultei a evidência indicada e confirmei que ela corresponde a esta transação e ao estado mostrado.</span>
                                            </label>
                                            {reconciliationResolveError && <p role="alert" className="text-sm text-destructive">{reconciliationResolveError}</p>}
                                            {!resolutionCurrent && !reconciliationResolveError && <p role="alert" className="text-sm text-destructive">A fila foi atualizada. Feche esta janela e confira novamente a transação antes de registrar uma decisão.</p>}
                                            <div className="flex justify-end gap-2">
                                                <Button type="button" variant="outline" onClick={() => setReconciliationItemToResolve(null)} disabled={reconciliationResolving}>Voltar</Button>
                                                <Button type="submit" disabled={reconciliationResolving || !resolutionCurrent}>
                                                    {reconciliationResolving ? "Registrando..." : "Registrar decisão"}
                                                </Button>
                                            </div>
                                        </form>
                                    );
                                })()}
                            </DialogContent>
                        </Dialog>

                        {/* MODAL DETALHES DE INTEGRAÇÃO (ADMIN) */}
                        <Dialog open={isDetailsOpen} onOpenChange={handleDetailsOpenChange}>
                            <DialogContent className="max-w-xl bg-white rounded-xl shadow-xl p-0 overflow-hidden">
                                <DialogHeader className="p-6 pb-4 border-b bg-slate-50/70">
                                    <DialogTitle className="text-lg font-bold">
                                        Integração Mercado Pago - {selectedCompanyForDetails?.name}
                                    </DialogTitle>
                                    <DialogDescription className="sr-only">
                                        Consulte o status e os dados de configuração da integração desta empresa.
                                    </DialogDescription>
                                </DialogHeader>
                                <div className="p-6 space-y-5 text-sm">
                                    {loadingDetails ? (
                                        <p className="text-muted-foreground animate-pulse text-center py-6 font-medium">Carregando detalhes...</p>
                                    ) : integrationDetailsError ? (
                                        <div role="alert" className="space-y-3 rounded-md border border-red-200 bg-red-50 p-4 text-center text-sm text-red-800">
                                            <p>Não foi possível carregar os detalhes da integração. Tente novamente.</p>
                                            <Button variant="outline" size="sm" onClick={() => {
                                                if (selectedCompanyForDetails) void handleViewIntegrationDetails(selectedCompanyForDetails.id, selectedCompanyForDetails.name);
                                            }}>Tentar novamente</Button>
                                        </div>
                                    ) : companyIntegration ? (
                                        <>
                                            <div className="flex justify-between items-center pb-2 border-b border-slate-100">
                                                <span className="font-semibold text-slate-700">Status da Integração:</span>
                                                <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-semibold ${
                                                    companyIntegration.isActive 
                                                        ? "bg-emerald-50 text-emerald-700 border border-emerald-200" 
                                                        : "bg-slate-100 text-slate-700 border border-slate-200"
                                                }`}>
                                                    {companyIntegration.isActive ? "Ativo" : "Inativo"}
                                                </span>
                                            </div>

                                            <div className="space-y-2">
                                                <h4 className="text-xs font-bold text-slate-400 uppercase tracking-wider">1. Dados do Responsável</h4>
                                                <div className="grid grid-cols-2 gap-y-2 bg-slate-50 p-3 rounded-lg border border-slate-100 text-xs">
                                                    <div><strong className="text-slate-600">Nome:</strong> {companyIntegration.ownerName}</div>
                                                    <div><strong className="text-slate-600">CPF:</strong> {companyIntegration.ownerCpf}</div>
                                                    <div><strong className="text-slate-600">E-mail:</strong> {companyIntegration.ownerEmail || "-"}</div>
                                                    <div><strong className="text-slate-600">Telefone:</strong> {companyIntegration.ownerPhone || "-"}</div>
                                                </div>
                                            </div>

                                            <div className="space-y-2">
                                                <h4 className="text-xs font-bold text-slate-400 uppercase tracking-wider">2. Dados da Empresa (PJ)</h4>
                                                <div className="grid grid-cols-2 gap-y-2 bg-slate-50 p-3 rounded-lg border border-slate-100 text-xs">
                                                    <div><strong className="text-slate-600">Razão Social:</strong> {companyIntegration.businessName}</div>
                                                    <div><strong className="text-slate-600">Nome Fantasia:</strong> {companyIntegration.tradeName || "-"}</div>
                                                    <div><strong className="text-slate-600">CNPJ:</strong> {companyIntegration.cnpj}</div>
                                                    <div><strong className="text-slate-600">E-mail:</strong> {companyIntegration.businessEmail || "-"}</div>
                                                </div>
                                            </div>

                                            <div className="space-y-2">
                                                <h4 className="text-xs font-bold text-slate-400 uppercase tracking-wider">3. Credenciais de API</h4>
                                                <div className="bg-slate-50 p-3 rounded-lg border border-slate-100 space-y-2 text-xs">
                                                    <div className="flex flex-col gap-0.5">
                                                        <span className="font-semibold text-slate-600">Token de acesso:</span>
                                                        <span className="text-slate-700">{companyIntegration.hasAccessToken ? "Configurado" : "Ausente"}</span>
                                                    </div>
                                                    <div className="flex flex-col gap-0.5">
                                                        <span className="font-semibold text-slate-600">Chave pública:</span>
                                                        <span className="font-mono text-slate-700 bg-white border border-slate-200/60 rounded px-2 py-1 select-all">{companyIntegration.publicKey}</span>
                                                    </div>
                                                    {companyIntegration.clientId && (
                                                        <div className="flex flex-col gap-0.5">
                                                            <span className="font-semibold text-slate-600">ID do cliente:</span>
                                                            <span className="font-mono text-slate-700 bg-white border border-slate-200/60 rounded px-2 py-1 select-all">{companyIntegration.clientId}</span>
                                                        </div>
                                                    )}
                                                    <div className="flex flex-col gap-0.5">
                                                        <span className="font-semibold text-slate-600">Chave secreta do cliente:</span>
                                                        <span className="text-slate-700">{companyIntegration.hasClientSecret ? "Configurada" : "Ausente"}</span>
                                                    </div>
                                                </div>
                                            </div>

                                            <div className="space-y-2">
                                                <h4 className="text-xs font-bold text-slate-400 uppercase tracking-wider">4. Diagnóstico OAuth</h4>
                                                <div className="grid grid-cols-2 gap-y-2 bg-slate-50 p-3 rounded-lg border border-slate-100 text-xs">
                                                    <div><strong className="text-slate-600">ID de usuário no Mercado Pago:</strong> {companyIntegration.mercadoPagoUserId || "-"}</div>
                                                    <div><strong className="text-slate-600">Apelido:</strong> {companyIntegration.mercadoPagoNickname || "-"}</div>
                                                    <div><strong className="text-slate-600">Site:</strong> {companyIntegration.mercadoPagoSiteId || "-"}</div>
                                                    <div><strong className="text-slate-600">Token de atualização:</strong> {companyIntegration.hasRefreshToken ? "Salvo" : "Ausente"}</div>
                                                    <div><strong className="text-slate-600">Expira em:</strong> {companyIntegration.accessTokenExpiresAt ? new Date(companyIntegration.accessTokenExpiresAt).toLocaleString("pt-BR") : "-"}</div>
                                                    <div className="col-span-2"><strong className="text-slate-600">Última validação:</strong> {companyIntegration.lastTokenValidationStatus || "-"}</div>
                                                </div>
                                            </div>
                                        </>
                                    ) : (
                                        <p className="text-muted-foreground text-center py-6">Esta empresa não possui nenhuma integração com o Mercado Pago configurada.</p>
                                    )}
                                </div>
                                <div className="p-4 border-t bg-slate-50/70 flex justify-end">
                                    <Button onClick={() => handleDetailsOpenChange(false)} className="px-5 h-9 font-semibold">
                                        Fechar
                                    </Button>
                                </div>
                            </DialogContent>
                        </Dialog>

                        {/* MODAL DETALHES DO LOG DE TRANSAÇÃO */}
                        <Dialog open={isLogDetailsOpen} onOpenChange={setIsLogDetailsOpen}>
                            <DialogContent className="max-w-2xl bg-white rounded-xl shadow-xl p-0 overflow-hidden">
                                <DialogHeader className="p-6 pb-4 border-b bg-slate-50/70">
                                    <DialogTitle className="text-lg font-bold flex items-center gap-2">
                                        <Terminal className="w-5 h-5 text-indigo-600" />
                                        Detalhes do Log de Telemetria
                                    </DialogTitle>
                                    <DialogDescription className="sr-only">
                                        Consulte os metadados e a mensagem registrada para este evento de telemetria.
                                    </DialogDescription>
                                </DialogHeader>
                                <div className="p-6 space-y-4 text-sm">
                                    <div className="grid grid-cols-2 gap-4 bg-slate-50 p-3 rounded-lg border border-slate-100 text-xs">
                                        <div><strong className="text-slate-600">ID do Log:</strong> <span className="font-mono">{selectedLogForDetails?.id}</span></div>
                                        <div><strong className="text-slate-600">ID da Transação:</strong> <span className="font-mono">{selectedLogForDetails?.transactionId}</span></div>
                                        <div><strong className="text-slate-600">Empresa:</strong> {selectedLogForDetails?.companyName}</div>
                                        <div><strong className="text-slate-600">Máquina:</strong> {selectedLogForDetails?.machineName}</div>
                                        <div><strong className="text-slate-600">Valor da Transação:</strong> R$ {selectedLogForDetails?.transactionAmount.toFixed(2)}</div>
                                        <div>
                                            <strong className="text-slate-600">Data/Hora:</strong>{" "}
                                            {selectedLogForDetails?.timestamp && new Date(selectedLogForDetails.timestamp).toLocaleString("pt-BR")}
                                        </div>
                                    </div>

                                    <div className="space-y-1">
                                        <h4 className="text-xs font-bold text-slate-400 uppercase tracking-wider">Mensagem de Evento</h4>
                                        <div className={`p-3 rounded-lg border text-sm font-medium ${
                                            selectedLogForDetails?.logType === "Error" 
                                                ? "bg-red-50 text-red-700 border-red-200" 
                                                : "bg-slate-50 text-slate-700 border-slate-200"
                                        }`}>
                                            {selectedLogForDetails?.message}
                                        </div>
                                    </div>

                                    {selectedLogForDetails?.rawResponse && (
                                        <div className="space-y-1">
                                            <h4 className="text-xs font-bold text-slate-400 uppercase tracking-wider">Retorno da API Mercado Pago (JSON bruto)</h4>
                                            <pre className="bg-slate-900 text-slate-100 p-4 rounded-lg overflow-auto text-xs font-mono max-h-60 select-all leading-normal">
                                                {(() => {
                                                    try {
                                                        const parsed = JSON.parse(selectedLogForDetails.rawResponse);
                                                        return JSON.stringify(parsed, null, 2);
                                                    } catch {
                                                        return selectedLogForDetails.rawResponse;
                                                    }
                                                })()}
                                            </pre>
                                        </div>
                                    )}
                                </div>
                                <div className="p-4 border-t bg-slate-50/70 flex justify-end">
                                    <Button onClick={() => setIsLogDetailsOpen(false)} className="px-5 h-9 font-semibold">
                                        Fechar
                                    </Button>
                                </div>
                            </DialogContent>
                        </Dialog>
                    </main>
                </div>
            </div>
        </SidebarProvider>
    );
}
