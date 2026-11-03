import { SidebarProvider, SidebarTrigger } from "@/components/ui/sidebar";
import { AppSidebar } from "@/components/AppSidebar";
import { Activity, ArrowLeft, Save, Microchip, Network, Server, ArrowLeftRight, HardDriveDownload, RefreshCw, DoorOpen } from "lucide-react";
import { useNavigate, useParams } from "react-router-dom";
import { z } from "zod";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Input } from "@/components/ui/input";
import { Checkbox } from "@/components/ui/checkbox";
import {
    Form,
    FormControl,
    FormField,
    FormItem,
    FormLabel,
    FormMessage,
} from "@/components/ui/form";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import {
    Select,
    SelectContent,
    SelectItem,
    SelectTrigger,
    SelectValue,
} from "@/components/ui/select";
import { toast } from "sonner";
import { useEffect, useState } from "react";
import {
    getMachine,
    createMachine,
    updateMachine,
    getIntegration,
    getLocations,
    getTelemetryConnections,
    requestMdbStatus,
    startTelemetrySession,
    type TelemetryConnection,
} from "@/lib/api";
import { type Location } from "@/data/mockData";
import { getMdbStatusDetail } from "@/lib/mdbStatus";

const machineFormSchema = z.object({
    id: z.string().optional(),
    grupoId: z.number().min(0, "Grupo ID não pode ser negativo").optional(),
    serialNumber: z.string().min(1, "Serial Number é obrigatório"),
    modelo: z.string().min(1, "Modelo é obrigatório"),
    locationId: z.string().min(1, "Selecione uma localização"),
    firmwareVersion: z.string().optional(),
    useGsm: z.boolean().default(false),
    useWifi: z.boolean().default(false),
    useEthernet: z.boolean().default(false),
    connectionType: z.string().optional(),
    ativo: z.boolean().default(true),
    isAutoConnectEnabled: z.boolean().default(false),
    isLocationEnabled: z.boolean().default(false),
    // Rede GSM
    simPin: z.string().optional(),
    gprsApn: z.string().optional(),
    gprsUsername: z.string().optional(),
    gprsPassword: z.string().optional(),
    // Rede Wi-Fi
    wifiSsid: z.string().optional(),
    wifiPassword: z.string().optional(),
    // Rede IP
    ipAddress: z.string().optional(),
    ipMask: z.string().optional(),
    gateway: z.string().optional(),
    isDhcpEnabled: z.boolean().default(true),
    // Servidor
    serverUrl: z.string().optional(),
    serverPort: z.number().optional(),
    useHttps: z.boolean().default(true),
    clientId: z.string().optional(),
    machineId: z.string().optional(),
    webQueryIntervalMs: z.number().optional(),
    // Vending
    evaSecurityPassword: z.string().optional(),
    evaPasscode: z.string().optional(),
    evaBaudRateOption: z.string().default("0"),
    evaLowSpeedStartup: z.boolean().default(false),
    // FTP
    isFtpEnabled: z.boolean().default(false),
    ftpServerAddress: z.string().optional(),
    ftpServerPort: z.number().default(21),
    ftpUsername: z.string().optional(),
    ftpPassword: z.string().optional(),
    ftpDirectoryPath: z.string().default("/evadts/"),
    mercadoPagoEnabled: z.boolean().default(false),
    autoOpenSessionEnabled: z.boolean().default(false),
});

type MachineFormValues = z.infer<typeof machineFormSchema>;

export default function MachineFormPage() {
    const navigate = useNavigate();
    const { id } = useParams();
    const isEditMode = !!id;
    const [isMpIntegrationActive, setIsMpIntegrationActive] = useState(false);
    const [locations, setLocations] = useState<Location[]>([]);
    const [mdbConnection, setMdbConnection] = useState<TelemetryConnection | null>(null);
    const [mdbBusy, setMdbBusy] = useState(false);

    const form = useForm<MachineFormValues>({
        resolver: zodResolver(machineFormSchema),
        defaultValues: {
            useGsm: false,
            useWifi: false,
            useEthernet: false,
            ativo: true,
            isDhcpEnabled: true,
            useHttps: true,
            isFtpEnabled: false,
            ftpServerPort: 21,
            ftpDirectoryPath: "/evadts/",
            evaBaudRateOption: "0",
            locationId: "",
            mercadoPagoEnabled: false,
            autoOpenSessionEnabled: false,
        },
    });

    const { watch } = form;
    const useGsm = watch("useGsm");
    const useWifi = watch("useWifi");
    const useEthernet = watch("useEthernet");

    useEffect(() => {
        // Buscar se integração está ativa
        getIntegration().then(data => {
            setIsMpIntegrationActive(data.isActive);
        }).catch(err => {
            console.error("Erro ao buscar integracao para a maquina:", err);
        });

        getLocations().then(data => {
            setLocations(data);
            if (!isEditMode && data.length > 0) {
                form.setValue("locationId", data[0].id);
            }
        }).catch(err => {
            console.error("Erro ao buscar localizações para a máquina:", err);
            toast.error("Erro ao carregar localizações.");
        });
    }, []);

    useEffect(() => {
        if (isEditMode && id) {
            getMachine(id).then(machine => {
                if (machine) {
                    form.reset({
                        id: machine.id,
                        serialNumber: machine.serialNumber,
                        modelo: machine.name,
                        locationId: machine.locationId || locations.find(location => location.name === machine.clientName)?.id || "",
                        useWifi: true,
                        ativo: machine.status === "online",
                        isDhcpEnabled: true,
                        useHttps: true,
                        ftpServerPort: 21,
                        ftpDirectoryPath: "/evadts/",
                        evaBaudRateOption: "0",
                        mercadoPagoEnabled: machine.mercadoPagoEnabled || false,
                        autoOpenSessionEnabled: machine.autoOpenSessionEnabled || false,
                    });
                }
            }).catch(err => {
                console.error("Erro ao buscar detalhes da máquina:", err);
            });
        }
    }, [id, isEditMode, form, locations]);

    useEffect(() => {
        if (!isEditMode || !id) return;
        getTelemetryConnections()
            .then(items => setMdbConnection(items.find(item => item.machineId === id) ?? null))
            .catch(() => setMdbConnection(null));
    }, [id, isEditMode]);

    function onSubmit(data: MachineFormValues) {
        console.log("Form Submitted:", data);
        
        const selectedLocation = locations.find(location => location.id === data.locationId);
        const machineData = {
            name: data.modelo,
            serialNumber: data.serialNumber,
            status: data.ativo ? "online" as const : "offline" as const,
            locationId: data.locationId,
            location: selectedLocation?.name || "Localização principal",
            clientName: selectedLocation?.name || "",
            mercadoPagoEnabled: data.mercadoPagoEnabled,
            autoOpenSessionEnabled: data.autoOpenSessionEnabled,
        };

        const savePromise = isEditMode && id
            ? updateMachine(id, machineData)
            : createMachine(machineData);

        savePromise
            .then(() => {
                toast.success(`Máquina ${isEditMode ? "atualizada" : "cadastrada"} com sucesso!`);
                navigate("/machines");
            })
            .catch(err => {
                console.error("Erro ao salvar máquina:", err);
                toast.error(err.message || "Erro ao salvar máquina.");
            });
    }

    async function refreshMdbConnection() {
        if (!id) return;
        const items = await getTelemetryConnections();
        setMdbConnection(items.find(item => item.machineId === id) ?? null);
    }

    async function handleMdbStatusRequest() {
        if (!id) return;
        setMdbBusy(true);
        try {
            const result = await requestMdbStatus(id);
            setMdbConnection(current => current ? {
                ...current,
                mdbStatus: result.mdbStatus,
                mdbStatusUpdatedAt: result.mdbStatusUpdatedAt,
                mdbStatusFresh: true,
            } : current);
            toast.success("Status MDB atualizado.");
            await refreshMdbConnection();
        } catch (error) {
            toast.error((error as Error).message);
        } finally {
            setMdbBusy(false);
        }
    }

    async function handleOpenSession() {
        if (!id) return;
        setMdbBusy(true);
        try {
            await startTelemetrySession(id);
            toast.success("Sessão solicitada.");
            await refreshMdbConnection();
        } catch (error) {
            toast.error((error as Error).message);
        } finally {
            setMdbBusy(false);
        }
    }

    return (
        <SidebarProvider>
            <div className="min-h-screen flex w-full">
                <AppSidebar />
                <div className="flex-1 flex flex-col min-w-0">
                    <header className="h-12 flex items-center border-b bg-card px-4 gap-3">
                        <SidebarTrigger />
                        <h1 className="text-sm font-semibold text-foreground">
                            {isEditMode ? "Editar Máquina" : "Nova Máquina"}
                        </h1>
                    </header>
                    
                    <main className="flex-1 p-6 space-y-6 overflow-auto bg-muted/20">
                        <div className="flex items-center justify-between mb-6">
                            <div className="flex items-center gap-4">
                                <Button variant="ghost" size="icon" onClick={() => navigate("/machines")}>
                                    <ArrowLeft className="w-5 h-5" />
                                </Button>
                                <div>
                                    <h2 className="text-2xl font-bold tracking-tight">
                                        {isEditMode ? "Edição de Equipamento" : "Cadastro de Equipamento"}
                                    </h2>
                                    <p className="text-sm text-muted-foreground">
                                        Preencha os dados técnicos da máquina e suas configurações de rede.
                                    </p>
                                </div>
                            </div>
                            <Button onClick={form.handleSubmit(onSubmit)} className="gap-2">
                                <Save className="w-4 h-4" />
                                Salvar Equipamento
                            </Button>
                        </div>

                        <div className="bg-card border rounded-lg shadow-sm">
                            <Form {...form}>
                                <form onSubmit={form.handleSubmit(onSubmit)} className="p-6">
                                    <Tabs defaultValue="hardware" className="w-full">
                                        <TabsList className="mb-6 bg-muted/50 p-1 rounded-md inline-flex w-full overflow-x-auto justify-start border">
                                            <TabsTrigger value="hardware" className="gap-2"><Microchip className="w-4 h-4" /> Hardware</TabsTrigger>
                                            <TabsTrigger value="rede" className="gap-2"><Network className="w-4 h-4" /> Rede</TabsTrigger>
                                            <TabsTrigger value="servidor" className="gap-2"><Server className="w-4 h-4" /> Servidor</TabsTrigger>
                                            <TabsTrigger value="vending" className="gap-2"><ArrowLeftRight className="w-4 h-4" /> Vending</TabsTrigger>
                                            <TabsTrigger value="ftp" className="gap-2"><HardDriveDownload className="w-4 h-4" /> FTP</TabsTrigger>
                                        </TabsList>

                                        {/* ABA HARDWARE */}
                                        <TabsContent value="hardware" className="space-y-6 animate-in fade-in-50 duration-300">
                                            <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
                                                <FormField control={form.control} name="serialNumber" render={({ field }) => (
                                                    <FormItem>
                                                        <FormLabel>Serial Number *</FormLabel>
                                                        <FormControl><Input placeholder="Ex: SN-123456" {...field} /></FormControl>
                                                        <FormMessage />
                                                    </FormItem>
                                                )} />
                                                <FormField control={form.control} name="modelo" render={({ field }) => (
                                                    <FormItem>
                                                        <FormLabel>Modelo *</FormLabel>
                                                        <FormControl><Input placeholder="Ex: Modelo X" {...field} /></FormControl>
                                                        <FormMessage />
                                                    </FormItem>
                                                )} />
                                            </div>

                                            <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
                                                <FormField control={form.control} name="locationId" render={({ field }) => (
                                                    <FormItem>
                                                        <FormLabel>Localização *</FormLabel>
                                                        <Select value={field.value || ""} onValueChange={field.onChange}>
                                                            <FormControl><SelectTrigger><SelectValue placeholder="Selecione uma localização..." /></SelectTrigger></FormControl>
                                                            <SelectContent>
                                                                {locations.filter(l => l.id && l.id.trim() !== "").map(location => (
                                                                    <SelectItem key={location.id} value={location.id}>
                                                                        {location.name}
                                                                    </SelectItem>
                                                                ))}
                                                            </SelectContent>
                                                        </Select>
                                                        <FormMessage />
                                                    </FormItem>
                                                )} />
                                                <FormField control={form.control} name="firmwareVersion" render={({ field }) => (
                                                    <FormItem>
                                                        <FormLabel>Versão Firmware</FormLabel>
                                                        <FormControl><Input placeholder="Ex: v1.2.3" {...field} /></FormControl>
                                                        <FormMessage />
                                                    </FormItem>
                                                )} />
                                            </div>

                                            <div className="p-4 border rounded-md bg-muted/10">
                                                <h3 className="text-sm font-semibold mb-4 text-foreground">Recursos de Comunicação Habilitados</h3>
                                                <div className="flex flex-wrap gap-8">
                                                    <FormField control={form.control} name="useGsm" render={({ field }) => (
                                                        <FormItem className="flex flex-row items-center space-x-3 space-y-0">
                                                            <FormControl><Checkbox checked={field.value} onCheckedChange={field.onChange} /></FormControl>
                                                            <FormLabel className="font-normal cursor-pointer">Possui GSM</FormLabel>
                                                        </FormItem>
                                                    )} />
                                                    <FormField control={form.control} name="useWifi" render={({ field }) => (
                                                        <FormItem className="flex flex-row items-center space-x-3 space-y-0">
                                                            <FormControl><Checkbox checked={field.value} onCheckedChange={field.onChange} /></FormControl>
                                                            <FormLabel className="font-normal cursor-pointer">Possui Wi-Fi</FormLabel>
                                                        </FormItem>
                                                    )} />
                                                    <FormField control={form.control} name="useEthernet" render={({ field }) => (
                                                        <FormItem className="flex flex-row items-center space-x-3 space-y-0">
                                                            <FormControl><Checkbox checked={field.value} onCheckedChange={field.onChange} /></FormControl>
                                                            <FormLabel className="font-normal cursor-pointer">Possui Ethernet</FormLabel>
                                                        </FormItem>
                                                    )} />
                                                </div>
                                            </div>

                                            <div className="grid grid-cols-1 md:grid-cols-4 gap-6 items-end">
                                                <FormField control={form.control} name="connectionType" render={({ field }) => (
                                                    <FormItem>
                                                        <FormLabel>Conexão Ativa (Firmware)</FormLabel>
                                                        <Select onValueChange={field.onChange} defaultValue={field.value}>
                                                            <FormControl><SelectTrigger><SelectValue placeholder="Automático" /></SelectTrigger></FormControl>
                                                            <SelectContent>
                                                                <SelectItem value="GSM">GSM</SelectItem>
                                                                <SelectItem value="WIFI">Wi-Fi</SelectItem>
                                                                <SelectItem value="ETHERNET">Ethernet</SelectItem>
                                                            </SelectContent>
                                                        </Select>
                                                        <FormMessage />
                                                    </FormItem>
                                                )} />
                                                <FormField control={form.control} name="ativo" render={({ field }) => (
                                                    <FormItem className="flex flex-row items-center space-x-3 space-y-0 p-3 border rounded-md h-10">
                                                        <FormControl><Checkbox checked={field.value} onCheckedChange={field.onChange} /></FormControl>
                                                        <FormLabel className="font-semibold cursor-pointer">Ativo</FormLabel>
                                                    </FormItem>
                                                )} />
                                                <FormField control={form.control} name="isAutoConnectEnabled" render={({ field }) => (
                                                    <FormItem className="flex flex-row items-center space-x-3 space-y-0 p-3 border rounded-md h-10">
                                                        <FormControl><Checkbox checked={field.value} onCheckedChange={field.onChange} /></FormControl>
                                                        <FormLabel className="font-semibold cursor-pointer">Auto Conectar</FormLabel>
                                                    </FormItem>
                                                )} />
                                                <FormField control={form.control} name="isLocationEnabled" render={({ field }) => (
                                                    <FormItem className="flex flex-row items-center space-x-3 space-y-0 p-3 border rounded-md h-10">
                                                        <FormControl><Checkbox checked={field.value} onCheckedChange={field.onChange} /></FormControl>
                                                        <FormLabel className="font-semibold cursor-pointer">Localização ativa</FormLabel>
                                                    </FormItem>
                                                )} />
                                            </div>

                                            {/* CONFIGURAÇÃO DE PAGAMENTO */}
                                            <div className="p-4 border rounded-md bg-muted/10 mt-6">
                                                <h3 className="text-sm font-semibold mb-4 text-foreground">Configuração de Pagamento</h3>
                                                {isMpIntegrationActive ? (
                                                    <FormField control={form.control} name="mercadoPagoEnabled" render={({ field }) => (
                                                        <FormItem className="flex flex-row items-center space-x-3 space-y-0">
                                                            <FormControl>
                                                                <Checkbox checked={field.value} onCheckedChange={field.onChange} />
                                                            </FormControl>
                                                            <div>
                                                                <FormLabel className="font-semibold cursor-pointer">Habilitar Cobrança via Mercado Pago (Pix)</FormLabel>
                                                                <p className="text-xs text-muted-foreground mt-0.5">Permite que compradores façam pagamentos via Pix por esta máquina.</p>
                                                            </div>
                                                        </FormItem>
                                                    )} />
                                                ) : (
                                                    <div className="text-sm text-yellow-600 bg-yellow-50 border border-yellow-200 rounded p-3 select-none">
                                                        A integração com o Mercado Pago está inativa para a sua empresa. Ative-a nas <strong>Configurações</strong> do sistema para poder habilitar cobranças nesta máquina.
                                                    </div>
                                                )}
                                            </div>
                                        </TabsContent>

                                        {/* ABA REDE */}
                                        <TabsContent value="rede" className="space-y-6 animate-in fade-in-50 duration-300">
                                            {!useGsm && !useWifi && !useEthernet && (
                                                <div className="p-4 border border-dashed rounded-md text-center text-muted-foreground">
                                                    Nenhum recurso de comunicação foi ativado na aba de Hardware.
                                                </div>
                                            )}

                                            {useGsm && (
                                                <div className="p-5 border border-primary/20 bg-primary/5 rounded-md space-y-4">
                                                    <h3 className="text-sm font-semibold flex items-center gap-2 text-primary">
                                                        <Network className="w-4 h-4" /> Configuração GSM
                                                    </h3>
                                                    <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
                                                        <FormField control={form.control} name="simPin" render={({ field }) => (
                                                            <FormItem><FormLabel>SIM PIN</FormLabel><FormControl><Input {...field} /></FormControl></FormItem>
                                                        )} />
                                                        <FormField control={form.control} name="gprsApn" render={({ field }) => (
                                                            <FormItem><FormLabel>APN</FormLabel><FormControl><Input {...field} /></FormControl></FormItem>
                                                        )} />
                                                        <FormField control={form.control} name="gprsUsername" render={({ field }) => (
                                                            <FormItem><FormLabel>Usuário</FormLabel><FormControl><Input {...field} /></FormControl></FormItem>
                                                        )} />
                                                        <FormField control={form.control} name="gprsPassword" render={({ field }) => (
                                                            <FormItem><FormLabel>Senha</FormLabel><FormControl><Input type="password" {...field} /></FormControl></FormItem>
                                                        )} />
                                                    </div>
                                                </div>
                                            )}

                                            {useWifi && (
                                                <div className="p-5 border border-primary/20 bg-primary/5 rounded-md space-y-4">
                                                    <h3 className="text-sm font-semibold flex items-center gap-2 text-primary">
                                                        <Network className="w-4 h-4" /> Configuração Wi-Fi
                                                    </h3>
                                                    <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                                                        <FormField control={form.control} name="wifiSsid" render={({ field }) => (
                                                            <FormItem><FormLabel>SSID</FormLabel><FormControl><Input {...field} /></FormControl></FormItem>
                                                        )} />
                                                        <FormField control={form.control} name="wifiPassword" render={({ field }) => (
                                                            <FormItem><FormLabel>Senha</FormLabel><FormControl><Input type="password" {...field} /></FormControl></FormItem>
                                                        )} />
                                                    </div>
                                                </div>
                                            )}

                                            {(useWifi || useEthernet) && (
                                                <div className="p-5 border border-primary/20 bg-primary/5 rounded-md space-y-4">
                                                    <h3 className="text-sm font-semibold flex items-center gap-2 text-primary">
                                                        <Network className="w-4 h-4" /> Endereçamento IP (Ethernet/Wi-Fi)
                                                    </h3>
                                                    <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
                                                        <FormField control={form.control} name="ipAddress" render={({ field }) => (
                                                            <FormItem><FormLabel>IP</FormLabel><FormControl><Input {...field} /></FormControl></FormItem>
                                                        )} />
                                                        <FormField control={form.control} name="ipMask" render={({ field }) => (
                                                            <FormItem><FormLabel>Máscara</FormLabel><FormControl><Input {...field} /></FormControl></FormItem>
                                                        )} />
                                                        <FormField control={form.control} name="gateway" render={({ field }) => (
                                                            <FormItem><FormLabel>Gateway</FormLabel><FormControl><Input {...field} /></FormControl></FormItem>
                                                        )} />
                                                    </div>
                                                    <div className="pt-2">
                                                        <FormField control={form.control} name="isDhcpEnabled" render={({ field }) => (
                                                            <FormItem className="flex flex-row items-center space-x-3 space-y-0">
                                                                <FormControl><Checkbox checked={field.value} onCheckedChange={field.onChange} /></FormControl>
                                                                <FormLabel className="font-normal cursor-pointer">DHCP Habilitado</FormLabel>
                                                            </FormItem>
                                                        )} />
                                                    </div>
                                                </div>
                                            )}
                                        </TabsContent>

                                        {/* ABA SERVIDOR */}
                                        <TabsContent value="servidor" className="space-y-6 animate-in fade-in-50 duration-300">
                                            <div className="grid grid-cols-1 md:grid-cols-3 gap-6 items-end">
                                                <div className="md:col-span-2">
                                                    <FormField control={form.control} name="serverUrl" render={({ field }) => (
                                                        <FormItem><FormLabel>URL do Servidor</FormLabel><FormControl><Input placeholder="https://api.exemplo.com" {...field} /></FormControl></FormItem>
                                                    )} />
                                                </div>
                                                <FormField control={form.control} name="serverPort" render={({ field }) => (
                                                    <FormItem><FormLabel>Porta</FormLabel><FormControl><Input type="number" {...field} onChange={e => field.onChange(parseInt(e.target.value))} /></FormControl></FormItem>
                                                )} />
                                            </div>
                                            
                                            <div className="grid grid-cols-1 md:grid-cols-3 gap-6 items-end">
                                                <FormField control={form.control} name="clientId" render={({ field }) => (
                                                    <FormItem><FormLabel>Client ID</FormLabel><FormControl><Input {...field} /></FormControl></FormItem>
                                                )} />
                                                <FormField control={form.control} name="machineId" render={({ field }) => (
                                                    <FormItem><FormLabel>Machine ID</FormLabel><FormControl><Input {...field} /></FormControl></FormItem>
                                                )} />
                                                <FormField control={form.control} name="webQueryIntervalMs" render={({ field }) => (
                                                    <FormItem><FormLabel>Intervalo Poll (ms)</FormLabel><FormControl><Input type="number" {...field} onChange={e => field.onChange(parseInt(e.target.value))} /></FormControl></FormItem>
                                                )} />
                                            </div>

                                            <FormField control={form.control} name="useHttps" render={({ field }) => (
                                                <FormItem className="flex flex-row items-center space-x-3 space-y-0 p-4 border rounded-md">
                                                    <FormControl><Checkbox checked={field.value} onCheckedChange={field.onChange} /></FormControl>
                                                    <FormLabel className="font-semibold cursor-pointer">Segurança (SSL/TLS)</FormLabel>
                                                </FormItem>
                                            )} />
                                        </TabsContent>

                                        {/* ABA VENDING */}
                                        <TabsContent value="vending" className="space-y-6 animate-in fade-in-50 duration-300">
                                            <div className="p-4 border rounded-md bg-muted/10 space-y-4">
                                                <div>
                                                    <h3 className="text-sm font-semibold text-foreground">Sessão MDB</h3>
                                                    <p className="text-xs text-muted-foreground mt-1">
                                                        Controle como esta máquina disponibiliza uma sessão para o usuário.
                                                    </p>
                                                </div>

                                                <FormField control={form.control} name="autoOpenSessionEnabled" render={({ field }) => (
                                                    <FormItem className="flex flex-row items-start space-x-3 space-y-0 p-4 border rounded-md bg-background">
                                                        <FormControl>
                                                            <Checkbox checked={field.value} onCheckedChange={field.onChange} />
                                                        </FormControl>
                                                        <div>
                                                            <FormLabel className="font-semibold cursor-pointer">Abrir sessão automaticamente</FormLabel>
                                                            <p className="text-xs text-muted-foreground mt-1">
                                                                Quando o MDB estiver disponível, o sistema abrirá e reabrirá sessões sem ação do operador.
                                                                O acompanhamento de telemetria será ativado ao salvar.
                                                            </p>
                                                        </div>
                                                    </FormItem>
                                                )} />

                                                {isEditMode && (
                                                    <div className="p-4 border rounded-md bg-background space-y-3">
                                                        <div className="flex flex-col sm:flex-row sm:items-start justify-between gap-3">
                                                            <div className="flex gap-3">
                                                                <Activity className="w-5 h-5 mt-0.5 text-muted-foreground" />
                                                                <div>
                                                                    <div className="flex items-center gap-2 flex-wrap">
                                                                        <span className="text-sm font-semibold">
                                                                            {getMdbStatusDetail(mdbConnection?.mdbStatus).label}
                                                                        </span>
                                                                        <Badge variant={mdbConnection?.mdbStatusFresh ? "default" : "outline"}>
                                                                            {mdbConnection?.mdbStatusFresh ? "Atual" : "Desatualizado"}
                                                                        </Badge>
                                                                    </div>
                                                                    <p className="text-xs text-muted-foreground mt-1">
                                                                        {getMdbStatusDetail(mdbConnection?.mdbStatus).description}
                                                                    </p>
                                                                    <p className="text-xs text-muted-foreground mt-1">
                                                                        Última atualização: {mdbConnection?.mdbStatusUpdatedAt
                                                                            ? new Date(mdbConnection.mdbStatusUpdatedAt).toLocaleString("pt-BR")
                                                                            : "—"}
                                                                    </p>
                                                                </div>
                                                            </div>
                                                            <div className="flex gap-2 flex-wrap">
                                                                <Button
                                                                    type="button"
                                                                    variant="outline"
                                                                    size="sm"
                                                                    disabled={mdbBusy || !mdbConnection?.online || !mdbConnection?.monitoringEnabled}
                                                                    onClick={handleMdbStatusRequest}
                                                                >
                                                                    <RefreshCw className={`w-4 h-4 mr-2 ${mdbBusy ? "animate-spin" : ""}`} />
                                                                    Consultar status
                                                                </Button>
                                                                {!mdbConnection?.activeSession && (
                                                                    <Button
                                                                        type="button"
                                                                        size="sm"
                                                                        disabled={
                                                                            mdbBusy ||
                                                                            !mdbConnection?.online ||
                                                                            !mdbConnection?.monitoringEnabled ||
                                                                            (mdbConnection?.manualStartRequiresMdb &&
                                                                                (!mdbConnection.mdbStatusFresh || mdbConnection.mdbStatus !== "enabled_state"))
                                                                        }
                                                                        onClick={handleOpenSession}
                                                                    >
                                                                        <DoorOpen className="w-4 h-4 mr-2" />
                                                                        Abrir sessão
                                                                    </Button>
                                                                )}
                                                            </div>
                                                        </div>
                                                    </div>
                                                )}
                                            </div>

                                            <div className="grid grid-cols-1 md:grid-cols-3 gap-6">
                                                <FormField control={form.control} name="evaSecurityPassword" render={({ field }) => (
                                                    <FormItem><FormLabel>EVA Security</FormLabel><FormControl><Input type="password" {...field} /></FormControl></FormItem>
                                                )} />
                                                <FormField control={form.control} name="evaPasscode" render={({ field }) => (
                                                    <FormItem><FormLabel>EVA Passcode</FormLabel><FormControl><Input type="password" {...field} /></FormControl></FormItem>
                                                )} />
                                                <FormField control={form.control} name="evaBaudRateOption" render={({ field }) => (
                                                    <FormItem>
                                                        <FormLabel>Baud Rate</FormLabel>
                                                        <Select onValueChange={field.onChange} defaultValue={field.value}>
                                                            <FormControl><SelectTrigger><SelectValue placeholder="Selecione..." /></SelectTrigger></FormControl>
                                                            <SelectContent>
                                                                <SelectItem value="0">9600 bps</SelectItem>
                                                                <SelectItem value="1">19200 bps</SelectItem>
                                                            </SelectContent>
                                                        </Select>
                                                    </FormItem>
                                                )} />
                                            </div>
                                            <FormField control={form.control} name="evaLowSpeedStartup" render={({ field }) => (
                                                <FormItem className="flex flex-row items-center space-x-3 space-y-0 p-4 border rounded-md">
                                                    <FormControl><Checkbox checked={field.value} onCheckedChange={field.onChange} /></FormControl>
                                                    <FormLabel className="font-semibold cursor-pointer">Low Speed Startup (2400-init)</FormLabel>
                                                </FormItem>
                                            )} />
                                        </TabsContent>

                                        {/* ABA FTP */}
                                        <TabsContent value="ftp" className="space-y-6 animate-in fade-in-50 duration-300">
                                            <FormField control={form.control} name="isFtpEnabled" render={({ field }) => (
                                                <FormItem className="flex flex-row items-center space-x-3 space-y-0 p-4 border rounded-md bg-muted/10">
                                                    <FormControl><Checkbox checked={field.value} onCheckedChange={field.onChange} /></FormControl>
                                                    <FormLabel className="font-semibold cursor-pointer text-lg">Habilitar FTP</FormLabel>
                                                </FormItem>
                                            )} />

                                            {form.watch("isFtpEnabled") && (
                                                <div className="space-y-6 pt-4 border-t">
                                                    <div className="grid grid-cols-1 md:grid-cols-4 gap-6 items-end">
                                                        <div className="md:col-span-3">
                                                            <FormField control={form.control} name="ftpServerAddress" render={({ field }) => (
                                                                <FormItem><FormLabel>Host do Servidor</FormLabel><FormControl><Input {...field} /></FormControl></FormItem>
                                                            )} />
                                                        </div>
                                                        <FormField control={form.control} name="ftpServerPort" render={({ field }) => (
                                                            <FormItem><FormLabel>Porta FTP</FormLabel><FormControl><Input type="number" {...field} onChange={e => field.onChange(parseInt(e.target.value))} /></FormControl></FormItem>
                                                        )} />
                                                    </div>
                                                    <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
                                                        <FormField control={form.control} name="ftpUsername" render={({ field }) => (
                                                            <FormItem><FormLabel>Usuário</FormLabel><FormControl><Input {...field} /></FormControl></FormItem>
                                                        )} />
                                                        <FormField control={form.control} name="ftpPassword" render={({ field }) => (
                                                            <FormItem><FormLabel>Senha</FormLabel><FormControl><Input type="password" {...field} /></FormControl></FormItem>
                                                        )} />
                                                    </div>
                                                    <FormField control={form.control} name="ftpDirectoryPath" render={({ field }) => (
                                                        <FormItem><FormLabel>Diretório do Firmware</FormLabel><FormControl><Input placeholder="/evadts/" {...field} /></FormControl></FormItem>
                                                    )} />
                                                </div>
                                            )}
                                        </TabsContent>
                                    </Tabs>
                                </form>
                            </Form>
                        </div>
                    </main>
                </div>
            </div>
        </SidebarProvider>
    );
}
