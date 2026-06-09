import { SidebarProvider, SidebarTrigger } from "@/components/ui/sidebar";
import { AppSidebar } from "@/components/AppSidebar";
import { useAuth } from "@/contexts/AuthContext";
import { useNavigate } from "react-router-dom";
import { useEffect, useState } from "react";
import { getUsers, getCompanies, type PaginatedUsers, type PaginatedCompanies } from "@/lib/api";
import { 
    Users as UsersIcon, 
    ShieldAlert, 
    LogOut, 
    Search, 
    ChevronLeft, 
    ChevronRight,
    Building2,
    Activity
} from "lucide-react";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Input } from "@/components/ui/input";
import { Button } from "@/components/ui/button";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Skeleton } from "@/components/ui/skeleton";

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

    const isAdmin = user?.role === "Admin";

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
                                                            <TableCell className="px-6 py-4"><Skeleton className="h-4 w-36" /></TableCell>
                                                            <TableCell className="px-6 py-4"><Skeleton className="h-5 w-16 rounded-full" /></TableCell>
                                                            <TableCell className="px-6 py-4"><Skeleton className="h-4 w-24" /></TableCell>
                                                        </TableRow>
                                                    ))
                                                ) : !userData || userData.items.length === 0 ? (
                                                    <TableRow>
                                                        <TableCell colSpan={5} className="text-center py-12 text-slate-400 font-medium font-sans">
                                                            Nenhum usuário localizado.
                                                        </TableCell>
                                                    </TableRow>
                                                ) : (
                                                    userData.items.map((usr) => (
                                                        <TableRow key={usr.id} className="hover:bg-slate-50/50 transition-colors">
                                                            <TableCell className="px-6 py-3.5 font-medium text-slate-900">{usr.name}</TableCell>
                                                            <TableCell className="px-6 py-3.5 font-mono text-xs text-slate-600">{usr.email}</TableCell>
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
                                                    <TableHead className="px-6 font-semibold text-slate-700">Dono / Criador</TableHead>
                                                    <TableHead className="px-6 font-semibold text-slate-700">Sócios Vinculados</TableHead>
                                                    <TableHead className="px-6 font-semibold text-slate-700">Data de Criação</TableHead>
                                                </TableRow>
                                            </TableHeader>
                                            <TableBody>
                                                {companiesLoading ? (
                                                    Array.from({ length: 3 }).map((_, idx) => (
                                                        <TableRow key={idx}>
                                                            <TableCell className="px-6 py-4"><Skeleton className="h-4 w-40" /></TableCell>
                                                            <TableCell className="px-6 py-4"><Skeleton className="h-4 w-32" /></TableCell>
                                                            <TableCell className="px-6 py-4"><Skeleton className="h-4 w-48" /></TableCell>
                                                            <TableCell className="px-6 py-4"><Skeleton className="h-4 w-24" /></TableCell>
                                                        </TableRow>
                                                    ))
                                                ) : !companyData || companyData.items.length === 0 ? (
                                                    <TableRow>
                                                        <TableCell colSpan={4} className="text-center py-12 text-slate-400 font-medium font-sans">
                                                            Nenhuma empresa localizada.
                                                        </TableCell>
                                                    </TableRow>
                                                ) : (
                                                    companyData.items.map((comp) => (
                                                        <TableRow key={comp.id} className="hover:bg-slate-50/50 transition-colors">
                                                            <TableCell className="px-6 py-3.5 font-bold text-slate-900">{comp.name}</TableCell>
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
                        </Tabs>
                    </main>
                </div>
            </div>
        </SidebarProvider>
    );
}
