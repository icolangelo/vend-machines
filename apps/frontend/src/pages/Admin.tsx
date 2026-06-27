import { SidebarProvider, SidebarTrigger } from "@/components/ui/sidebar";
import { AppSidebar } from "@/components/AppSidebar";
import { useAuth } from "@/contexts/AuthContext";
import { useNavigate } from "react-router-dom";
import { useEffect, useState } from "react";
import { getUsers, getCompanies, getGlobalSettings, updateGlobalSettings, getCompanyIntegration, getAdminLogs, type PaginatedUsers, type PaginatedCompanies, type SystemSettings, type MercadoPagoIntegration, type PaginatedAdminLogs, type AdminLog } from "@/lib/api";
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
    Save
} from "lucide-react";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Input } from "@/components/ui/input";
import { Button } from "@/components/ui/button";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Skeleton } from "@/components/ui/skeleton";
import { Dialog, DialogContent, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Badge } from "@/components/ui/badge";

export default function Admin() {
    const { user, logout } = useAuth();
    const navigate = useNavigate();

    // Estados da listagem de usuários
    const [userData, setUserData] = useState<PaginatedUsers | null>(null);
    const [userPage, setUserPage] = useState(1);
    const [userSearch, setUserSearch] = useState("");
    const [userSearchInput, setUserSearchInput] = useState("");
    const [usersLoading, setUsersLoading] = useState(true);

    // Estados da listagem de empresas
    const [companyData, setCompanyData] = useState<PaginatedCompanies | null>(null);
    const [companyPage, setCompanyPage] = useState(1);
    const [companySearch, setCompanySearch] = useState("");
    const [companySearchInput, setCompanySearchInput] = useState("");
    const [companiesLoading, setCompaniesLoading] = useState(true);

    const isAdmin = user?.email === "dev.ivan@gmail.com";

    const [globalSettings, setGlobalSettings] = useState<SystemSettings | null>(null);
    const [settingsLoading, setSettingsLoading] = useState(false);
    const [savingSettings, setSavingSettings] = useState(false);
    const [feeInput, setFeeInput] = useState<number>(5.0);

    // Detalhes da Integração da Empresa (Admin)
    const [isDetailsOpen, setIsDetailsOpen] = useState(false);
    const [selectedCompanyForDetails, setSelectedCompanyForDetails] = useState<{ id: string, name: string } | null>(null);
    const [companyIntegration, setCompanyIntegration] = useState<MercadoPagoIntegration | null>(null);
    const [loadingDetails, setLoadingDetails] = useState(false);

    // Estados dos Logs (Admin)
    const [logsData, setLogsData] = useState<PaginatedAdminLogs | null>(null);
    const [logsPage, setLogsPage] = useState(1);
    const [logsSearch, setLogsSearch] = useState("");
    const [logsSearchInput, setLogsSearchInput] = useState("");
    const [logsLoading, setLogsLoading] = useState(true);
    const [selectedLogForDetails, setSelectedLogForDetails] = useState<AdminLog | null>(null);
    const [isLogDetailsOpen, setIsLogDetailsOpen] = useState(false);

    // Efeito para carregar logs
    useEffect(() => {
        if (!isAdmin) return;
        setLogsLoading(true);
        getAdminLogs(logsPage, 10, logsSearch)
            .then(data => {
                setLogsData(data);
                setLogsLoading(false);
            })
            .catch(err => {
                console.error("Erro ao carregar logs administrativos:", err);
                setLogsLoading(false);
            });
    }, [logsPage, logsSearch, isAdmin]);

    const handleLogsSearchSubmit = (e: React.FormEvent) => {
        e.preventDefault();
        setLogsPage(1);
        setLogsSearch(logsSearchInput);
    };

    const handleViewLogDetails = (log: AdminLog) => {
        setSelectedLogForDetails(log);
        setIsLogDetailsOpen(true);
    };

    const handleViewIntegrationDetails = async (companyId: string, companyName: string) => {
        setSelectedCompanyForDetails({ id: companyId, name: companyName });
        setIsDetailsOpen(true);
        setLoadingDetails(true);
        try {
            const data = await getCompanyIntegration(companyId);
            setCompanyIntegration(data);
        } catch (err) {
            console.error("Erro ao carregar detalhes da integração da empresa:", err);
            setCompanyIntegration(null);
        } finally {
            setLoadingDetails(false);
        }
    };

    // Efeito para carregar as configurações de pagamento globais
    useEffect(() => {
        if (!isAdmin) return;
        setSettingsLoading(true);
        getGlobalSettings()
            .then(data => {
                setGlobalSettings(data);
                setFeeInput(data.applicationFeePercent);
                setSettingsLoading(false);
            })
            .catch(err => {
                console.error("Erro ao carregar configurações de pagamento:", err);
                setSettingsLoading(false);
            });
    }, [isAdmin]);

    const handleSaveGlobalSettings = async () => {
        if (feeInput < 0 || feeInput > 100) {
            alert("A taxa da plataforma deve estar entre 0% e 100%.");
            return;
        }

        setSavingSettings(true);
        try {
            await updateGlobalSettings({
                applicationFeePercent: feeInput
            });
            alert("Configurações de pagamento atualizadas com sucesso!");
        } catch (err) {
            console.error("Erro ao salvar taxa global:", err);
            alert("Erro ao salvar taxa global.");
        } finally {
            setSavingSettings(false);
        }
    };

    // Efeito para carregar usuários
    useEffect(() => {
        if (!isAdmin) return;
        setUsersLoading(true);
        getUsers(userPage, 8, userSearch)
            .then(data => {
                setUserData(data);
                setUsersLoading(false);
            })
            .catch(err => {
                console.error("Erro ao carregar usuários:", err);
                setUsersLoading(false);
            });
    }, [userPage, userSearch, isAdmin]);

    // Efeito para carregar empresas
    useEffect(() => {
        if (!isAdmin) return;
        setCompaniesLoading(true);
        getCompanies(companyPage, 8, companySearch)
            .then(data => {
                setCompanyData(data);
                setCompaniesLoading(false);
            })
            .catch(err => {
                console.error("Erro ao carregar empresas:", err);
                setCompaniesLoading(false);
            });
    }, [companyPage, companySearch, isAdmin]);

    const handleUserSearchSubmit = (e: React.FormEvent) => {
        e.preventDefault();
        setUserPage(1);
        setUserSearch(userSearchInput);
    };

    const handleCompanySearchSubmit = (e: React.FormEvent) => {
        e.preventDefault();
        setCompanyPage(1);
        setCompanySearch(companySearchInput);
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
                            Voltar para o Dashboard
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
                                Perfil: <strong className="text-blue-600 font-semibold">{user?.role}</strong>
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

                        <Tabs defaultValue="users" className="w-full">
                            <TabsList className="bg-slate-100 p-1 rounded-lg border border-slate-200 inline-flex mb-4">
                                <TabsTrigger value="users" className="px-4 py-1.5 text-sm font-medium transition-all rounded-md data-[state=active]:bg-white data-[state=active]:shadow-sm">
                                    Usuários
                                </TabsTrigger>
                                <TabsTrigger value="companies" className="px-4 py-1.5 text-sm font-medium transition-all rounded-md data-[state=active]:bg-white data-[state=active]:shadow-sm">
                                    Empresas
                                </TabsTrigger>
                                <TabsTrigger value="payments" className="px-4 py-1.5 text-sm font-medium transition-all rounded-md data-[state=active]:bg-white data-[state=active]:shadow-sm">
                                    Pagamentos
                                </TabsTrigger>
                                <TabsTrigger value="logs" className="px-4 py-1.5 text-sm font-medium transition-all rounded-md data-[state=active]:bg-white data-[state=active]:shadow-sm">
                                    Logs de Transações
                                </TabsTrigger>
                            </TabsList>

                            {/* ABA DE USUÁRIOS */}
                            <TabsContent value="users" className="space-y-4 outline-none">
                                <Card className="border border-slate-200 bg-white shadow-sm">
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
                                                {usersLoading ? (
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
                                                                <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-semibold ${
                                                                    usr.role === "Admin" 
                                                                        ? "bg-blue-50 text-blue-700 border border-blue-200" 
                                                                        : "bg-slate-100 text-slate-700 border border-slate-200"
                                                                }`}>
                                                                    {usr.role}
                                                                </span>
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
                                                        disabled={userPage === 1 || usersLoading}
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
                                                        disabled={userPage === userData.totalPages || usersLoading}
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
                            <TabsContent value="companies" className="space-y-4 outline-none">
                                <Card className="border border-slate-200 bg-white shadow-sm">
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
                                        <Table>
                                            <TableHeader className="bg-slate-50/70">
                                                <TableRow>
                                                    <TableHead className="px-6 font-semibold text-slate-700">Nome da Empresa</TableHead>
                                                    <TableHead className="px-6 font-semibold text-slate-700">CNPJ</TableHead>
                                                    <TableHead className="px-6 font-semibold text-slate-700">Dono / Criador</TableHead>
                                                    <TableHead className="px-6 font-semibold text-slate-700">Sócios Vinculados</TableHead>
                                                    <TableHead className="px-6 font-semibold text-slate-700">Data de Criação</TableHead>
                                                    <TableHead className="px-6 font-semibold text-slate-700">Integrações</TableHead>
                                                    <TableHead className="px-6 font-semibold text-slate-700 text-right">Ações</TableHead>
                                                </TableRow>
                                            </TableHeader>
                                            <TableBody>
                                                {companiesLoading ? (
                                                    Array.from({ length: 3 }).map((_, idx) => (
                                                        <TableRow key={idx}>
                                                            <TableCell className="px-6 py-4"><Skeleton className="h-4 w-40" /></TableCell>
                                                            <TableCell className="px-6 py-4"><Skeleton className="h-4 w-32" /></TableCell>
                                                            <TableCell className="px-6 py-4"><Skeleton className="h-4 w-32" /></TableCell>
                                                            <TableCell className="px-6 py-4"><Skeleton className="h-4 w-48" /></TableCell>
                                                            <TableCell className="px-6 py-4"><Skeleton className="h-4 w-24" /></TableCell>
                                                            <TableCell className="px-6 py-4"><Skeleton className="h-4 w-24" /></TableCell>
                                                            <TableCell className="px-6 py-4 text-right"><Skeleton className="h-8 w-16 ml-auto" /></TableCell>
                                                        </TableRow>
                                                    ))
                                                ) : !companyData || companyData.items.length === 0 ? (
                                                    <TableRow>
                                                        <TableCell colSpan={7} className="text-center py-12 text-slate-400 font-medium font-sans">
                                                            Nenhuma empresa localizada.
                                                        </TableCell>
                                                    </TableRow>
                                                ) : (
                                                    companyData.items.map((comp) => (
                                                        <TableRow key={comp.id} className="hover:bg-slate-50/50 transition-colors">
                                                            <TableCell className="px-6 py-3.5 font-bold text-slate-900">{comp.name}</TableCell>
                                                            <TableCell className="px-6 py-3.5 font-mono text-xs text-slate-600">{comp.cnpj || "-"}</TableCell>
                                                            <TableCell className="px-6 py-3.5 font-semibold text-blue-600">{comp.createdBy}</TableCell>
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
                                                                <Button
                                                                    variant="outline"
                                                                    size="sm"
                                                                    onClick={() => handleViewIntegrationDetails(comp.id, comp.name)}
                                                                    className="h-8 text-xs font-semibold"
                                                                >
                                                                    Detalhes
                                                                </Button>
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
                                                        disabled={companyPage === 1 || companiesLoading}
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
                                                        disabled={companyPage === companyData.totalPages || companiesLoading}
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
                            <TabsContent value="payments" className="space-y-4 outline-none">
                                <Card className="border border-slate-200 bg-white shadow-sm max-w-2xl">
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
                                        {settingsLoading ? (
                                            <div className="space-y-2 py-4">
                                                <Skeleton className="h-4 w-1/3" />
                                                <Skeleton className="h-10 w-full" />
                                            </div>
                                        ) : (
                                            <div className="space-y-4">
                                                <div className="space-y-1.5">
                                                    <label htmlFor="platform-fee" className="text-sm font-semibold text-slate-700">
                                                        Taxa de Comissão do Site (%)
                                                    </label>
                                                    <p className="text-xs text-muted-foreground">
                                                        Esta porcentagem será enviada como comissão da plataforma (`application_fee`) nas cobranças. Se o Mercado Pago recusar a taxa para Pix, a cobrança será rejeitada e nenhum QR Code será gerado.
                                                    </p>
                                                    <div className="flex items-center gap-3 mt-2">
                                                        <div className="relative max-w-[150px] w-full">
                                                            <Input
                                                                id="platform-fee"
                                                                type="number"
                                                                step="0.1"
                                                                min="0"
                                                                max="100"
                                                                className="pr-8 text-lg font-mono-data text-right focus:border-indigo-500"
                                                                value={feeInput}
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
                                                        disabled={savingSettings}
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
                            </TabsContent>

                            {/* ABA DE LOGS DE TRANSAÇÕES */}
                            <TabsContent value="logs" className="space-y-4 outline-none">
                                <Card className="border border-slate-200 bg-white shadow-sm">
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
                                                {logsLoading ? (
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
                        </Tabs>

                        {/* MODAL DETALHES DE INTEGRAÇÃO (ADMIN) */}
                        <Dialog open={isDetailsOpen} onOpenChange={setIsDetailsOpen}>
                            <DialogContent className="max-w-xl bg-white rounded-xl shadow-xl p-0 overflow-hidden">
                                <DialogHeader className="p-6 pb-4 border-b bg-slate-50/70">
                                    <DialogTitle className="text-lg font-bold">
                                        Integração Mercado Pago - {selectedCompanyForDetails?.name}
                                    </DialogTitle>
                                </DialogHeader>
                                <div className="p-6 space-y-5 text-sm">
                                    {loadingDetails ? (
                                        <p className="text-muted-foreground animate-pulse text-center py-6 font-medium">Carregando detalhes...</p>
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
                                                <h4 className="text-xs font-bold text-slate-400 uppercase tracking-wider">3. Credenciais de API (Mascaradas)</h4>
                                                <div className="bg-slate-50 p-3 rounded-lg border border-slate-100 space-y-2 text-xs">
                                                    <div className="flex flex-col gap-0.5">
                                                        <span className="font-semibold text-slate-600">Access Token:</span>
                                                        <span className="font-mono text-slate-700 bg-white border border-slate-200/60 rounded px-2 py-1 select-all">{companyIntegration.accessToken}</span>
                                                    </div>
                                                    <div className="flex flex-col gap-0.5">
                                                        <span className="font-semibold text-slate-600">Public Key:</span>
                                                        <span className="font-mono text-slate-700 bg-white border border-slate-200/60 rounded px-2 py-1 select-all">{companyIntegration.publicKey}</span>
                                                    </div>
                                                    {companyIntegration.clientId && (
                                                        <div className="flex flex-col gap-0.5">
                                                            <span className="font-semibold text-slate-600">Client ID:</span>
                                                            <span className="font-mono text-slate-700 bg-white border border-slate-200/60 rounded px-2 py-1 select-all">{companyIntegration.clientId}</span>
                                                        </div>
                                                    )}
                                                    {companyIntegration.clientSecret && (
                                                        <div className="flex flex-col gap-0.5">
                                                            <span className="font-semibold text-slate-600">Client Secret:</span>
                                                            <span className="font-mono text-slate-700 bg-white border border-slate-200/60 rounded px-2 py-1 select-all">{companyIntegration.clientSecret}</span>
                                                        </div>
                                                    )}
                                                </div>
                                            </div>
                                        </>
                                    ) : (
                                        <p className="text-muted-foreground text-center py-6">Esta empresa não possui nenhuma integração com o Mercado Pago configurada.</p>
                                    )}
                                </div>
                                <div className="p-4 border-t bg-slate-50/70 flex justify-end">
                                    <Button onClick={() => setIsDetailsOpen(false)} className="px-5 h-9 font-semibold">
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
