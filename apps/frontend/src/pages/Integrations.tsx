import { SidebarProvider, SidebarTrigger } from "@/components/ui/sidebar";
import { AppSidebar } from "@/components/AppSidebar";
import { LogOut, ArrowRight, Power, PowerOff, ShieldCheck, User, Building, CheckCircle2, RefreshCw, XCircle } from "lucide-react";
import { useState, useEffect } from "react";
import { useNavigate } from "react-router-dom";
import { useAuth } from "@/contexts/AuthContext";
import { getIntegration, toggleIntegration, getOauthConfig, disconnectIntegration, type MercadoPagoIntegration } from "@/lib/api";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle, CardDescription, CardFooter } from "@/components/ui/card";
import { useToast } from "@/components/ui/use-toast";
import { Badge } from "@/components/ui/badge";

export default function Integrations() {
    const navigate = useNavigate();
    const { logout, user } = useAuth();
    const { toast } = useToast();

    const [integration, setIntegration] = useState<MercadoPagoIntegration | null>(null);
    const [loading, setLoading] = useState(true);

    useEffect(() => {
        if (sessionStorage.getItem("isAuthenticated") !== "true") {
            navigate("/");
            return;
        }
        loadIntegration();
    }, [navigate]);

    const loadIntegration = async () => {
        setLoading(true);
        try {
            const data = await getIntegration();
            setIntegration(data);
        } catch (err) {
            console.error("Erro ao carregar integração:", err);
            toast({
                title: "Erro",
                description: "Não foi possível carregar a integração do Mercado Pago.",
                variant: "destructive"
            });
        } finally {
            setLoading(false);
        }
    };

    const handleToggleStatus = async () => {
        if (!integration) return;

        const newStatus = !integration.isActive;
        const actionText = newStatus ? "habilitar" : "desabilitar";

        if (!newStatus) {
            const confirmed = window.confirm("Deseja realmente desabilitar a integração? As máquinas associadas não aceitarão pagamentos via Pix temporariamente.");
            if (!confirmed) return;
        }

        try {
            await toggleIntegration(newStatus);
            setIntegration({ ...integration, isActive: newStatus });
            toast({
                title: newStatus ? "Integração Habilitada" : "Integração Desativada",
                description: `A integração do Mercado Pago foi ${newStatus ? "habilitada" : "desativada"} com sucesso.`
            });
        } catch (err) {
            console.error(err);
            toast({
                title: "Erro",
                description: `Ocorreu um erro ao ${actionText} a integração.`,
                variant: "destructive"
            });
        }
    };

    const handleConnectOauth = async () => {
        try {
            setLoading(true);
            const config = await getOauthConfig();
            const isTest = import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true" || config.clientId === "";

            if (isTest) {
                toast({
                    title: "Ambiente de Testes / Simulado",
                    description: "Redirecionando para autenticação simulada..."
                });
                setTimeout(() => {
                    window.location.href = `${window.location.origin}/?code=dummy_mock_oauth_code_${Date.now()}`;
                }, 1200);
            } else {
                const authUrl = `https://auth.mercadopago.com/authorization?client_id=${config.clientId}&response_type=code&platform_id=mp&redirect_uri=${encodeURIComponent(config.redirectUri)}`;
                window.location.href = authUrl;
            }
        } catch (err: any) {
            console.error(err);
            toast({
                title: "Erro",
                description: err.message || "Não foi possível iniciar a conexão com o Mercado Pago.",
                variant: "destructive"
            });
            setLoading(false);
        }
    };

    const handleDisconnect = async () => {
        const confirmed = window.confirm("Deseja realmente desconectar sua conta do Mercado Pago? Isso removerá todas as credenciais vinculadas e suas máquinas não poderão aceitar Pix.");
        if (!confirmed) return;

        try {
            setLoading(true);
            await disconnectIntegration();
            setIntegration(null);
            toast({
                title: "Desconectado",
                description: "Sua conta do Mercado Pago foi desconectada com sucesso."
            });
            await loadIntegration();
        } catch (err: any) {
            console.error(err);
            toast({
                title: "Erro",
                description: err.message || "Ocorreu um erro ao desconectar a conta.",
                variant: "destructive"
            });
            setLoading(false);
        }
    };

    const isConnected = !!(integration && integration.ownerName);

    return (
        <SidebarProvider>
            <div className="min-h-screen flex w-full bg-slate-50/50">
                <AppSidebar />
                <div className="flex-1 flex flex-col min-w-0">
                    <header className="h-12 flex items-center border-b bg-card px-4 gap-3">
                        <SidebarTrigger />
                        <h1 className="text-sm font-semibold text-foreground">Integrações</h1>
                        <div className="ml-auto flex items-center gap-4">
                            <span className="text-xs text-muted-foreground">
                                Empresa: <strong className="text-blue-600 font-semibold">{user?.companyName || "Nenhuma"}</strong>
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
                            <h2 className="text-2xl font-bold tracking-tight">Integrações de Pagamento</h2>
                            <p className="text-muted-foreground">
                                Conecte a conta do Mercado Pago da sua empresa para habilitar transações automáticas via Pix com split de tarifa para a plataforma.
                            </p>
                        </div>

                        {loading ? (
                            <div className="flex flex-col items-center justify-center h-[40vh] gap-3">
                                <RefreshCw className="w-8 h-8 text-sky-500 animate-spin" />
                                <p className="text-muted-foreground font-medium">Carregando integrações...</p>
                            </div>
                        ) : (
                            <div className="max-w-3xl space-y-6">
                                <Card className="border border-slate-200 bg-white shadow-sm relative overflow-hidden group">
                                    <div className="absolute top-0 left-0 w-full h-1.5 bg-gradient-to-r from-sky-400 to-blue-600" />

                                    <CardHeader className="pb-4">
                                        <div className="flex items-center justify-between">
                                            <div className="flex items-center gap-3">
                                                <div className="h-12 w-12 rounded-xl bg-sky-50 border border-sky-100 flex items-center justify-center text-sky-600 shadow-sm">
                                                    <svg className="w-8 h-8" viewBox="0 0 40 40" fill="none" xmlns="http://www.w3.org/2000/svg">
                                                        <circle cx="20" cy="20" r="18" fill="#009EE3" />
                                                        <path d="M12 20C12 18.5 13.5 16.5 15.5 16.5C17.5 16.5 18 18 19 19.5C20 21 20.5 22.5 22.5 22.5C24.5 22.5 26 20.5 26 19M19 19.5C18.5 20.5 17 21.5 15.5 21.5C14 21.5 12.5 20 12.5 19.5M26 19C26.5 18 25.5 17.5 24.5 17.5C23.5 17.5 22.5 18.5 22 19.5" stroke="white" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round" />
                                                    </svg>
                                                </div>
                                                <div>
                                                    <CardTitle className="text-xl font-bold">Mercado Pago</CardTitle>
                                                    <CardDescription className="text-xs">
                                                        Checkout Transparente via Pix
                                                    </CardDescription>
                                                </div>
                                            </div>

                                            <Badge variant={isConnected && integration?.isActive ? "success" : "secondary"} className={`text-xs font-semibold px-2.5 py-1 ${isConnected && integration?.isActive ? "bg-emerald-50 text-emerald-700 border-emerald-200" : "bg-slate-100 text-slate-600"}`}>
                                                {isConnected ? (integration?.isActive ? "Conectado e Ativo" : "Pausado") : "Não Configurado"}
                                            </Badge>
                                        </div>
                                    </CardHeader>

                                    <CardContent className="space-y-6">
                                        <p className="text-sm text-slate-600 leading-relaxed">
                                            A conexão é realizada diretamente por meio do fluxo oficial do Mercado Pago (OAuth 2.0).
                                            Isso garante máxima segurança, pois a plataforma não armazena suas chaves de API secretas brutas.
                                            Além disso, permite a divisão automática da taxa da plataforma (<strong className="text-indigo-600">split de comissão</strong>) na criação de cada QR Code Pix.
                                        </p>

                                        {isConnected ? (
                                            <div className="bg-slate-50 border border-slate-200/60 rounded-xl p-4 space-y-4">
                                                <div className="flex items-center gap-2 text-emerald-700 font-semibold text-sm">
                                                    <CheckCircle2 className="w-5 h-5 shrink-0" />
                                                    <span>Conta vinculada via OAuth com sucesso</span>
                                                </div>

                                                <div className="grid grid-cols-1 md:grid-cols-2 gap-4 text-xs pt-2 border-t border-slate-200/50">
                                                    <div className="space-y-3">
                                                        <div className="flex items-center gap-2">
                                                            <User className="w-4 h-4 text-slate-400" />
                                                            <div>
                                                                <p className="text-slate-400 font-medium">Sócio Responsável</p>
                                                                <p className="text-slate-800 font-semibold mt-0.5">{integration.ownerName}</p>
                                                                <p className="text-slate-500 mt-0.5">CPF: {integration.ownerCpf}</p>
                                                            </div>
                                                        </div>
                                                        {integration.ownerEmail && (
                                                            <p className="text-slate-500 pl-6">E-mail: {integration.ownerEmail}</p>
                                                        )}
                                                    </div>

                                                    <div className="space-y-3">
                                                        <div className="flex items-center gap-2">
                                                            <Building className="w-4 h-4 text-slate-400" />
                                                            <div>
                                                                <p className="text-slate-400 font-medium">Dados da Empresa</p>
                                                                <p className="text-slate-800 font-semibold mt-0.5">{integration.businessName}</p>
                                                                <p className="text-slate-500 mt-0.5">CNPJ: {integration.cnpj}</p>
                                                            </div>
                                                        </div>
                                                    </div>
                                                </div>

                                                <div className="flex items-center gap-2.5 bg-blue-50 border border-blue-100 text-blue-900 rounded-lg p-3 text-xs leading-relaxed">
                                                    <ShieldCheck className="w-4 h-4 text-blue-600 shrink-0 mt-0.5" />
                                                    <div>
                                                        A cobrança do Pix nas máquinas vinculadas à sua empresa utilizará as credenciais automáticas desta conta Mercado Pago.
                                                    </div>
                                                </div>
                                            </div>
                                        ) : (
                                            <div className="bg-sky-50 border border-sky-100 rounded-xl p-4 text-xs text-sky-900 flex gap-3">
                                                <ShieldCheck className="w-5 h-5 text-sky-600 shrink-0 mt-0.5" />
                                                <div>
                                                    <p className="font-semibold text-sm text-sky-950 mb-0.5">Pronto para começar?</p>
                                                    Ao conectar sua conta, as informações cadastrais do sócio e da empresa serão preenchidas automaticamente a partir de seu perfil, eliminando qualquer necessidade de digitação manual de chaves confidenciais.
                                                </div>
                                            </div>
                                        )}
                                    </CardContent>

                                    <CardFooter className="bg-slate-50/70 border-t border-slate-100 px-6 py-4 flex flex-col sm:flex-row gap-3 justify-between items-stretch sm:items-center">
                                        <div className="text-xs text-muted-foreground text-center sm:text-left">
                                            {integration?.updatedAt ? (
                                                <span>Última atualização: {new Date(integration.updatedAt).toLocaleString("pt-BR")}</span>
                                            ) : (
                                                <span>Conexão rápida e protegida via SSL</span>
                                            )}
                                        </div>

                                        <div className="flex gap-2 flex-col sm:flex-row">
                                            {isConnected ? (
                                                <>
                                                    <Button
                                                        variant="outline"
                                                        size="sm"
                                                        className={`flex items-center justify-center gap-1.5 h-9 font-medium transition-all ${integration?.isActive
                                                                ? "text-amber-700 hover:text-amber-800 hover:bg-amber-50 border-amber-200"
                                                                : "text-emerald-700 hover:text-emerald-800 hover:bg-emerald-50 border-emerald-200"
                                                            }`}
                                                        onClick={handleToggleStatus}
                                                    >
                                                        {integration?.isActive ? (
                                                            <>
                                                                <PowerOff className="w-4 h-4" />
                                                                Pausar Vendas Pix
                                                            </>
                                                        ) : (
                                                            <>
                                                                <Power className="w-4 h-4" />
                                                                Ativar Vendas Pix
                                                            </>
                                                        )}
                                                    </Button>

                                                    <Button
                                                        variant="destructive"
                                                        size="sm"
                                                        className="flex items-center justify-center gap-1.5 h-9 font-semibold"
                                                        onClick={handleDisconnect}
                                                    >
                                                        <XCircle className="w-4 h-4" />
                                                        Desconectar Conta
                                                    </Button>
                                                </>
                                            ) : (
                                                <Button
                                                    className="bg-sky-600 hover:bg-sky-700 text-white flex items-center justify-center gap-1.5 h-10 font-bold px-5 shadow-sm hover:shadow transition-all"
                                                    onClick={handleConnectOauth}
                                                >
                                                    Conectar com Mercado Pago
                                                    <ArrowRight className="w-4 h-4" />
                                                </Button>
                                            )}
                                        </div>
                                    </CardFooter>
                                </Card>
                            </div>
                        )}
                    </main>
                </div>
            </div>
        </SidebarProvider>
    );
}
