import { SidebarProvider, SidebarTrigger } from "@/components/ui/sidebar";
import { AppSidebar } from "@/components/AppSidebar";
import { LogOut, ArrowRight, Settings2, Power, PowerOff, ShieldCheck, User, Building, Key } from "lucide-react";
import { useState, useEffect } from "react";
import { useNavigate } from "react-router-dom";
import { useAuth } from "@/contexts/AuthContext";
import { getIntegration, saveIntegration, toggleIntegration, type MercadoPagoIntegration } from "@/lib/api";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Card, CardContent, CardHeader, CardTitle, CardDescription, CardFooter } from "@/components/ui/card";
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogFooter } from "@/components/ui/dialog";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { useToast } from "@/components/ui/use-toast";
import { Badge } from "@/components/ui/badge";

export default function Integrations() {
    const navigate = useNavigate();
    const { logout, user } = useAuth();
    const { toast } = useToast();

    const [integration, setIntegration] = useState<MercadoPagoIntegration | null>(null);
    const [loading, setLoading] = useState(true);
    const [saving, setSaving] = useState(false);
    const [isDialogOpen, setIsDialogOpen] = useState(false);
    const [activeTab, setActiveTab] = useState("owner");

    // Form states mapped directly to MercadoPagoIntegration
    const [formData, setFormData] = useState({
        ownerName: "",
        ownerCpf: "",
        ownerEmail: "",
        ownerPhone: "",
        businessName: "",
        tradeName: "",
        cnpj: "",
        businessEmail: "",
        businessPhone: "",
        accessToken: "",
        publicKey: "",
        clientId: "",
        clientSecret: ""
    });

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
            if (data && data.ownerName) {
                setFormData({
                    ownerName: data.ownerName || "",
                    ownerCpf: data.ownerCpf || "",
                    ownerEmail: data.ownerEmail || "",
                    ownerPhone: data.ownerPhone || "",
                    businessName: data.businessName || "",
                    tradeName: data.tradeName || "",
                    cnpj: data.cnpj || "",
                    businessEmail: data.businessEmail || "",
                    businessPhone: data.businessPhone || "",
                    accessToken: data.accessToken || "",
                    publicKey: data.publicKey || "",
                    clientId: data.clientId || "",
                    clientSecret: data.clientSecret || ""
                });
            }
        } catch (err) {
            console.error("Erro ao carregar integração:", err);
            toast({
                title: "Erro",
                description: "Não foi possível carregar as integrações.",
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
            const confirmed = window.confirm("Deseja realmente desabilitar a integração do Mercado Pago? As máquinas associadas não aceitarão Pix.");
            if (!confirmed) return;
        }

        try {
            await toggleIntegration(newStatus);
            setIntegration({ ...integration, isActive: newStatus });
            toast({
                title: newStatus ? "Integração Habilitada" : "Integração Desabilitada",
                description: `A integração do Mercado Pago foi ${newStatus ? "habilitada" : "desabilitada"} com sucesso.`
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

    const handleOpenConfigure = () => {
        setIsDialogOpen(true);
        setActiveTab("owner");
    };

    const handleSave = async (e: React.FormEvent) => {
        e.preventDefault();

        // Validações básicas
        if (!formData.ownerName || !formData.ownerCpf || !formData.businessName || !formData.cnpj || !formData.accessToken || !formData.publicKey) {
            toast({
                title: "Campos Obrigatórios",
                description: "Preencha todos os campos obrigatórios (*) do formulário.",
                variant: "destructive"
            });
            return;
        }

        setSaving(true);
        try {
            const updated: MercadoPagoIntegration = {
                ...integration,
                companyId: integration?.companyId || user?.companyId || "",
                ownerName: formData.ownerName,
                ownerCpf: formData.ownerCpf,
                ownerEmail: formData.ownerEmail,
                ownerPhone: formData.ownerPhone,
                businessName: formData.businessName,
                tradeName: formData.tradeName,
                cnpj: formData.cnpj,
                businessEmail: formData.businessEmail,
                businessPhone: formData.businessPhone,
                accessToken: formData.accessToken,
                publicKey: formData.publicKey,
                clientId: formData.clientId,
                clientSecret: formData.clientSecret,
                isActive: true // Ativa automaticamente ao salvar com sucesso
            };

            await saveIntegration(updated);
            setIsDialogOpen(false);
            loadIntegration();
            toast({
                title: "Sucesso",
                description: "Configurações da integração salvas com sucesso!"
            });
        } catch (err: any) {
            console.error(err);
            toast({
                title: "Erro ao salvar",
                description: err.message || "Ocorreu um erro ao salvar as credenciais.",
                variant: "destructive"
            });
        } finally {
            setSaving(false);
        }
    };

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
                            <h2 className="text-2xl font-bold tracking-tight">Integrações Disponíveis</h2>
                            <p className="text-muted-foreground">
                                Gerencie e conecte chaves de API externas e gateways de pagamento para a sua empresa.
                            </p>
                        </div>

                        {loading ? (
                            <div className="flex items-center justify-center h-[40vh]">
                                <p className="text-muted-foreground animate-pulse font-medium">Carregando integrações...</p>
                            </div>
                        ) : (
                            <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-6">
                                {/* CARD MERCADO PAGO */}
                                <Card className="border border-slate-200 bg-white shadow-sm flex flex-col hover:shadow-md transition-shadow relative overflow-hidden group">
                                    <div className="absolute top-0 left-0 w-full h-1.5 bg-sky-500" />
                                    
                                    <CardHeader className="pb-4">
                                        <div className="flex items-center justify-between">
                                            {/* Mercado Pago Custom Logo */}
                                            <div className="h-12 w-12 rounded-lg bg-sky-50 border border-sky-100 flex items-center justify-center text-sky-600">
                                                <svg className="w-8 h-8" viewBox="0 0 40 40" fill="none" xmlns="http://www.w3.org/2000/svg">
                                                    <circle cx="20" cy="20" r="18" fill="#009EE3" />
                                                    <path d="M12 20C12 18.5 13.5 16.5 15.5 16.5C17.5 16.5 18 18 19 19.5C20 21 20.5 22.5 22.5 22.5C24.5 22.5 26 20.5 26 19M19 19.5C18.5 20.5 17 21.5 15.5 21.5C14 21.5 12.5 20 12.5 19.5M26 19C26.5 18 25.5 17.5 24.5 17.5C23.5 17.5 22.5 18.5 22 19.5" stroke="white" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round" />
                                                </svg>
                                            </div>
                                            <Badge variant={integration?.isActive ? "success" : "secondary"} className={`text-xs font-semibold ${integration?.isActive ? "bg-emerald-50 text-emerald-700 border-emerald-200" : "bg-slate-100 text-slate-600"}`}>
                                                {integration?.isActive ? "Habilitado" : "Desabilitado"}
                                            </Badge>
                                        </div>
                                        <CardTitle className="text-xl font-bold mt-4">Mercado Pago</CardTitle>
                                        <CardDescription className="text-sm mt-1.5 line-clamp-3">
                                            Habilita o recebimento de pagamentos instantâneos de forma transparente nas suas máquinas via Pix.
                                        </CardDescription>
                                    </CardHeader>

                                    <CardContent className="flex-1 py-2 text-xs text-muted-foreground font-medium space-y-1">
                                        <div className="flex justify-between">
                                            <span>Tipo de Integração:</span>
                                            <span className="text-slate-800 font-semibold">Checkout Transparente</span>
                                        </div>
                                        <div className="flex justify-between">
                                            <span>Método de Recebimento:</span>
                                            <span className="text-slate-800 font-semibold">Pix Automático</span>
                                        </div>
                                        {integration?.updatedAt && (
                                            <div className="flex justify-between pt-2 border-t border-slate-100">
                                                <span>Última alteração:</span>
                                                <span>{new Date(integration.updatedAt).toLocaleDateString("pt-BR")}</span>
                                            </div>
                                        )}
                                    </CardContent>

                                    <CardFooter className="pt-4 border-t border-slate-100 flex gap-2">
                                        {integration?.ownerName ? (
                                            <>
                                                <Button
                                                    variant={integration.isActive ? "destructive-outline" : "outline"}
                                                    size="sm"
                                                    className="flex-1 flex items-center gap-1.5 h-9"
                                                    onClick={handleToggleStatus}
                                                >
                                                    {integration.isActive ? (
                                                        <>
                                                            <PowerOff className="w-4 h-4" />
                                                            Desabilitar
                                                        </>
                                                    ) : (
                                                        <>
                                                            <Power className="w-4 h-4" />
                                                            Habilitar
                                                        </>
                                                    )}
                                                </Button>

                                                {integration.isActive && (
                                                    <Button
                                                        variant="secondary"
                                                        size="sm"
                                                        className="flex items-center gap-1.5 h-9"
                                                        onClick={handleOpenConfigure}
                                                    >
                                                        <Settings2 className="w-4 h-4" />
                                                        Configurações
                                                    </Button>
                                                )}
                                            </>
                                        ) : (
                                            <Button
                                                className="w-full bg-sky-600 hover:bg-sky-700 flex items-center justify-center gap-1.5 h-9 text-white font-semibold"
                                                onClick={handleOpenConfigure}
                                            >
                                                Habilitar
                                                <ArrowRight className="w-4 h-4" />
                                            </Button>
                                        )}
                                    </CardFooter>
                                </Card>
                            </div>
                        )}

                        {/* CONFIGURATION MODAL DIALOG */}
                        <Dialog open={isDialogOpen} onOpenChange={setIsDialogOpen}>
                            <DialogContent className="max-w-2xl w-full p-0 overflow-hidden bg-white rounded-xl shadow-xl">
                                <DialogHeader className="p-6 pb-4 border-b bg-slate-50/70">
                                    <DialogTitle className="text-xl font-bold flex items-center gap-2">
                                        <svg className="w-6 h-6" viewBox="0 0 40 40" fill="none" xmlns="http://www.w3.org/2000/svg">
                                            <circle cx="20" cy="20" r="18" fill="#009EE3" />
                                            <path d="M12 20C12 18.5 13.5 16.5 15.5 16.5C17.5 16.5 18 18 19 19.5C20 21 20.5 22.5 22.5 22.5C24.5 22.5 26 20.5 26 19M19 19.5C18.5 20.5 17 21.5 15.5 21.5C14 21.5 12.5 20 12.5 19.5M26 19C26.5 18 25.5 17.5 24.5 17.5C23.5 17.5 22.5 18.5 22 19.5" stroke="white" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round" />
                                        </svg>
                                        Configurar Integração - Mercado Pago
                                    </DialogTitle>
                                    <p className="text-xs text-muted-foreground mt-1.5">
                                        Insira os dados do sócio, da empresa e as chaves fornecidas no painel de desenvolvedores do Mercado Pago.
                                    </p>
                                </DialogHeader>

                                <form onSubmit={handleSave} className="flex flex-col h-full">
                                    <div className="p-6 max-h-[60vh] overflow-y-auto">
                                        <Tabs value={activeTab} onValueChange={setActiveTab} className="w-full">
                                            <TabsList className="bg-slate-100 p-1 rounded-lg border border-slate-200 inline-flex w-full mb-6 gap-1">
                                                <TabsTrigger value="owner" className="flex-1 flex items-center justify-center gap-2 py-2 text-sm font-medium transition-all rounded-md data-[state=active]:bg-white data-[state=active]:shadow-sm">
                                                    <User className="w-4 h-4" />
                                                    1. Dono / Sócio
                                                </TabsTrigger>
                                                <TabsTrigger value="business" className="flex-1 flex items-center justify-center gap-2 py-2 text-sm font-medium transition-all rounded-md data-[state=active]:bg-white data-[state=active]:shadow-sm">
                                                    <Building className="w-4 h-4" />
                                                    2. Empresa
                                                </TabsTrigger>
                                                <TabsTrigger value="credentials" className="flex-1 flex items-center justify-center gap-2 py-2 text-sm font-medium transition-all rounded-md data-[state=active]:bg-white data-[state=active]:shadow-sm">
                                                    <Key className="w-4 h-4" />
                                                    3. Credenciais MP
                                                </TabsTrigger>
                                            </TabsList>

                                            {/* SEÇÃO DONO/SÓCIO */}
                                            <TabsContent value="owner" className="space-y-4 outline-none">
                                                <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                                                    <div className="space-y-1.5">
                                                        <label className="text-xs font-semibold text-slate-700">Nome Completo do Sócio *</label>
                                                        <Input
                                                            placeholder="Ex: João da Silva"
                                                            value={formData.ownerName}
                                                            onChange={e => setFormData({ ...formData, ownerName: e.target.value })}
                                                        />
                                                    </div>
                                                    <div className="space-y-1.5">
                                                        <label className="text-xs font-semibold text-slate-700">CPF do Responsável *</label>
                                                        <Input
                                                            placeholder="Ex: 000.000.000-00"
                                                            value={formData.ownerCpf}
                                                            onChange={e => setFormData({ ...formData, ownerCpf: e.target.value })}
                                                        />
                                                    </div>
                                                    <div className="space-y-1.5">
                                                        <label className="text-xs font-semibold text-slate-700">E-mail do Sócio</label>
                                                        <Input
                                                            type="email"
                                                            placeholder="Ex: socio@empresa.com"
                                                            value={formData.ownerEmail}
                                                            onChange={e => setFormData({ ...formData, ownerEmail: e.target.value })}
                                                        />
                                                    </div>
                                                    <div className="space-y-1.5">
                                                        <label className="text-xs font-semibold text-slate-700">Telefone do Sócio</label>
                                                        <Input
                                                            placeholder="Ex: (11) 98765-4321"
                                                            value={formData.ownerPhone}
                                                            onChange={e => setFormData({ ...formData, ownerPhone: e.target.value })}
                                                        />
                                                    </div>
                                                </div>
                                            </TabsContent>

                                            {/* SEÇÃO EMPRESA */}
                                            <TabsContent value="business" className="space-y-4 outline-none">
                                                <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                                                    <div className="space-y-1.5">
                                                        <label className="text-xs font-semibold text-slate-700">Razão Social *</label>
                                                        <Input
                                                            placeholder="Ex: Minha Empresa LTDA"
                                                            value={formData.businessName}
                                                            onChange={e => setFormData({ ...formData, businessName: e.target.value })}
                                                        />
                                                    </div>
                                                    <div className="space-y-1.5">
                                                        <label className="text-xs font-semibold text-slate-700">Nome Fantasia</label>
                                                        <Input
                                                            placeholder="Ex: Vending Express"
                                                            value={formData.tradeName}
                                                            onChange={e => setFormData({ ...formData, tradeName: e.target.value })}
                                                        />
                                                    </div>
                                                    <div className="space-y-1.5">
                                                        <label className="text-xs font-semibold text-slate-700">CNPJ da Empresa *</label>
                                                        <Input
                                                            placeholder="Ex: 00.000.000/0000-00"
                                                            value={formData.cnpj}
                                                            onChange={e => setFormData({ ...formData, cnpj: e.target.value })}
                                                        />
                                                    </div>
                                                    <div className="space-y-1.5">
                                                        <label className="text-xs font-semibold text-slate-700">E-mail Comercial</label>
                                                        <Input
                                                            type="email"
                                                            placeholder="Ex: comercial@empresa.com"
                                                            value={formData.businessEmail}
                                                            onChange={e => setFormData({ ...formData, businessEmail: e.target.value })}
                                                        />
                                                    </div>
                                                    <div className="space-y-1.5 md:col-span-2">
                                                        <label className="text-xs font-semibold text-slate-700">Telefone da Empresa</label>
                                                        <Input
                                                            placeholder="Ex: (11) 4004-0000"
                                                            value={formData.businessPhone}
                                                            onChange={e => setFormData({ ...formData, businessPhone: e.target.value })}
                                                        />
                                                    </div>
                                                </div>
                                            </TabsContent>

                                            {/* SEÇÃO CREDENCIAIS */}
                                            <TabsContent value="credentials" className="space-y-4 outline-none">
                                                <div className="bg-sky-50 border border-sky-100 rounded-lg p-3.5 flex gap-2.5 text-sky-800 text-xs leading-relaxed mb-4">
                                                    <ShieldCheck className="w-5 h-5 shrink-0 mt-0.5" />
                                                    <div>
                                                        As chaves confidenciais são salvas de forma criptografada no servidor e transmitidas de forma segura. Valores mascarados não serão alterados ao salvar.
                                                    </div>
                                                </div>

                                                <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                                                    <div className="space-y-1.5 md:col-span-2">
                                                        <label className="text-xs font-semibold text-slate-700">Access Token *</label>
                                                        <Input
                                                            type="password"
                                                            placeholder="APP_USR-..."
                                                            value={formData.accessToken}
                                                            onChange={e => setFormData({ ...formData, accessToken: e.target.value })}
                                                        />
                                                    </div>
                                                    <div className="space-y-1.5 md:col-span-2">
                                                        <label className="text-xs font-semibold text-slate-700">Public Key *</label>
                                                        <Input
                                                            placeholder="APP_USR-..."
                                                            value={formData.publicKey}
                                                            onChange={e => setFormData({ ...formData, publicKey: e.target.value })}
                                                        />
                                                    </div>
                                                    <div className="space-y-1.5">
                                                        <label className="text-xs font-semibold text-slate-700">Client ID (Opcional)</label>
                                                        <Input
                                                            placeholder="ID da aplicação no Mercado Pago"
                                                            value={formData.clientId}
                                                            onChange={e => setFormData({ ...formData, clientId: e.target.value })}
                                                        />
                                                    </div>
                                                    <div className="space-y-1.5">
                                                        <label className="text-xs font-semibold text-slate-700">Client Secret (Opcional)</label>
                                                        <Input
                                                            type="password"
                                                            placeholder="Chave secreta da aplicação"
                                                            value={formData.clientSecret}
                                                            onChange={e => setFormData({ ...formData, clientSecret: e.target.value })}
                                                        />
                                                    </div>
                                                </div>
                                            </TabsContent>
                                        </Tabs>
                                    </div>

                                    <DialogFooter className="p-6 border-t bg-slate-50/70 gap-2 flex-row justify-end">
                                        <Button variant="outline" type="button" onClick={() => setIsDialogOpen(false)} className="h-9">
                                            Cancelar
                                        </Button>
                                        {activeTab !== "credentials" ? (
                                            <Button
                                                type="button"
                                                onClick={() => setActiveTab(activeTab === "owner" ? "business" : "credentials")}
                                                className="bg-indigo-600 hover:bg-indigo-700 text-white font-semibold h-9"
                                            >
                                                Avançar
                                            </Button>
                                        ) : (
                                            <Button
                                                type="submit"
                                                disabled={saving}
                                                className="bg-sky-600 hover:bg-sky-700 text-white font-semibold h-9"
                                            >
                                                {saving ? "Salvando..." : "Salvar Configuração"}
                                            </Button>
                                        )}
                                    </DialogFooter>
                                </form>
                            </DialogContent>
                        </Dialog>
                    </main>
                </div>
            </div>
        </SidebarProvider>
    );
}
