import { SidebarProvider, SidebarTrigger } from "@/components/ui/sidebar";
import { AppSidebar } from "@/components/AppSidebar";
import { KpiCard } from "@/components/dashboard/KpiCard";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription } from "@/components/ui/dialog";
import { createLocation, deleteLocation, getLocations, updateLocation } from "@/lib/api";
import { type Location } from "@/data/mockData";
import { ArrowLeft, ArrowRight, Box, DollarSign, Edit, ExternalLink, MapPin, Plus, ShoppingCart, Trash2 } from "lucide-react";
import { useEffect, useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import { toast } from "sonner";

export default function Locations() {
    const navigate = useNavigate();
    const [locations, setLocations] = useState<Location[]>([]);
    const [loading, setLoading] = useState(true);
    const [isDialogOpen, setIsDialogOpen] = useState(false);
    const [editingLocation, setEditingLocation] = useState<Location | null>(null);
    const [locationName, setLocationName] = useState("");
    const [currentPage, setCurrentPage] = useState(1);
    const itemsPerPage = 20;

    const loadLocations = () => {
        setLoading(true);
        getLocations()
            .then(data => {
                setLocations(data);
                setLoading(false);
            })
            .catch(err => {
                console.error("Erro ao buscar localizações:", err);
                toast.error(err.message || "Erro ao buscar localizações.");
                setLoading(false);
            });
    };

    useEffect(() => {
        if (sessionStorage.getItem("isAuthenticated") !== "true") {
            navigate("/");
            return;
        }

        loadLocations();
    }, [navigate]);

    const { totalLocations, linkedMachines, totalRevenue, totalSales } = useMemo(() => {
        return {
            totalLocations: locations.length,
            linkedMachines: locations.reduce((acc, location) => acc + location.machineCount, 0),
            totalRevenue: locations.reduce((acc, location) => acc + location.revenue30d, 0),
            totalSales: locations.reduce((acc, location) => acc + location.totalSales30d, 0)
        };
    }, [locations]);

    const totalPages = Math.ceil(locations.length / itemsPerPage);
    const paginatedData = useMemo(() => {
        return locations.slice((currentPage - 1) * itemsPerPage, currentPage * itemsPerPage);
    }, [locations, currentPage]);

    const openCreateDialog = () => {
        setEditingLocation(null);
        setLocationName("");
        setIsDialogOpen(true);
    };

    const openEditDialog = (location: Location) => {
        setEditingLocation(location);
        setLocationName(location.name);
        setIsDialogOpen(true);
    };

    const handleSave = async () => {
        const name = locationName.trim();
        if (!name) {
            toast.error("Informe o nome da localização.");
            return;
        }

        try {
            if (editingLocation) {
                await updateLocation(editingLocation.id, name);
                toast.success("Localização atualizada com sucesso.");
            } else {
                await createLocation(name);
                toast.success("Localização cadastrada com sucesso.");
            }
            setIsDialogOpen(false);
            loadLocations();
        } catch (err: any) {
            toast.error(err.message || "Erro ao salvar localização.");
        }
    };

    const handleDelete = async (location: Location) => {
        if (location.machineCount > 0) {
            toast.error("Não é possível excluir uma localização com máquinas vinculadas.");
            return;
        }

        if (!window.confirm(`Excluir a localização "${location.name}"?`)) {
            return;
        }

        try {
            await deleteLocation(location.id);
            toast.success("Localização excluída com sucesso.");
            loadLocations();
        } catch (err: any) {
            toast.error(err.message || "Erro ao excluir localização.");
        }
    };

    return (
        <SidebarProvider>
            <div className="min-h-screen flex w-full">
                <AppSidebar />
                <div className="flex-1 flex flex-col min-w-0">
                    <header className="h-12 flex items-center border-b bg-card px-4 gap-3">
                        <SidebarTrigger />
                        <h1 className="text-sm font-semibold text-foreground">Localizações</h1>
                    </header>
                    <main className="flex-1 p-6 space-y-6 overflow-auto">
                        {loading ? (
                            <div className="flex items-center justify-center h-[50vh]">
                                <p className="text-muted-foreground animate-pulse font-medium">Carregando localizações...</p>
                            </div>
                        ) : (
                            <>
                                <div className="flex items-center justify-between gap-4">
                                    <h2 className="text-lg font-semibold tracking-tight">Visão Geral</h2>
                                    <Button onClick={openCreateDialog} size="sm" className="gap-2">
                                        <Plus className="w-4 h-4" />
                                        Nova Localização
                                    </Button>
                                </div>

                                <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
                                    <KpiCard title="Total de Localizações" value={totalLocations.toString()} icon={MapPin} subtitle="cadastradas" />
                                    <KpiCard title="Máquinas Vinculadas" value={linkedMachines.toString()} icon={Box} subtitle="em localizações" />
                                    <KpiCard title="Faturamento Gerado" value={`R$ ${totalRevenue.toLocaleString("pt-BR")}`} icon={DollarSign} subtitle="últimos 30 dias" />
                                    <KpiCard title="Volume de Vendas" value={totalSales.toLocaleString("pt-BR")} icon={ShoppingCart} subtitle="pagamentos aprovados" />
                                </div>

                                <div className="bg-card border rounded-lg flex flex-col animate-slide-up" style={{ animationDelay: "100ms" }}>
                                    <div className="px-5 py-4 border-b flex items-center justify-between gap-4">
                                        <h3 className="font-semibold text-foreground flex items-center gap-2">
                                            <MapPin className="h-4 w-4" /> Localizações da Empresa
                                        </h3>
                                        <span className="font-mono-data text-xs text-muted-foreground">
                                            {locations.length} itens encontrados
                                        </span>
                                    </div>

                                    <div className="overflow-x-auto">
                                        <table className="w-full text-sm">
                                            <thead>
                                                <tr className="border-b bg-muted/50">
                                                    <th className="text-left px-5 py-3 text-label font-medium w-3/5">Nome</th>
                                                    <th className="text-center px-5 py-3 text-label font-medium">Máquinas</th>
                                                    <th className="text-left px-5 py-3 text-label font-medium">Faturamento (30d)</th>
                                                    <th className="text-left px-5 py-3 text-label font-medium">Vendas (30d)</th>
                                                    <th className="px-5 py-3 w-24"></th>
                                                </tr>
                                            </thead>
                                            <tbody>
                                                {paginatedData.length === 0 ? (
                                                    <tr>
                                                        <td colSpan={5} className="px-5 py-8 text-center text-muted-foreground">
                                                            Nenhuma localização cadastrada.
                                                        </td>
                                                    </tr>
                                                ) : (
                                                    paginatedData.map((location) => (
                                                        <tr key={location.id} className="border-b last:border-b-0 hover:bg-muted/30 transition-colors duration-150">
                                                            <td className="px-5 py-4 font-medium text-foreground">
                                                                <button
                                                                    onClick={() => navigate("/machines", { state: { selectedLocationId: location.id } })}
                                                                    className="flex items-center gap-1.5 text-primary font-semibold hover:underline underline-offset-4 transition-colors text-left"
                                                                    title="Ver máquinas desta localização"
                                                                >
                                                                    {location.name}
                                                                    <ExternalLink className="w-3.5 h-3.5 opacity-70" />
                                                                </button>
                                                            </td>
                                                            <td className="px-5 py-4 text-center font-mono-data text-muted-foreground">
                                                                {location.machineCount}
                                                            </td>
                                                            <td className="px-5 py-4 font-medium">
                                                                R$ {location.revenue30d.toLocaleString("pt-BR", { minimumFractionDigits: 2 })}
                                                            </td>
                                                            <td className="px-5 py-4 font-mono-data text-muted-foreground">
                                                                {location.totalSales30d.toLocaleString("pt-BR")}
                                                            </td>
                                                            <td className="px-5 py-4">
                                                                <div className="flex items-center justify-end gap-1">
                                                                    <button
                                                                        onClick={() => openEditDialog(location)}
                                                                        className="p-1.5 text-muted-foreground hover:text-foreground hover:bg-muted rounded transition-colors"
                                                                        title="Editar localização"
                                                                    >
                                                                        <Edit className="w-4 h-4" />
                                                                    </button>
                                                                    <button
                                                                        onClick={() => handleDelete(location)}
                                                                        disabled={location.machineCount > 0}
                                                                        className="p-1.5 text-muted-foreground hover:text-destructive hover:bg-muted rounded transition-colors disabled:opacity-40 disabled:cursor-not-allowed"
                                                                        title={location.machineCount > 0 ? "Não é possível excluir com máquinas vinculadas" : "Excluir localização"}
                                                                    >
                                                                        <Trash2 className="w-4 h-4" />
                                                                    </button>
                                                                </div>
                                                            </td>
                                                        </tr>
                                                    ))
                                                )}
                                            </tbody>
                                        </table>
                                    </div>

                                    {totalPages > 1 && (
                                        <div className="px-5 py-3 border-t flex items-center justify-between">
                                            <span className="text-xs text-muted-foreground">
                                                Página {currentPage} de {totalPages}
                                            </span>
                                            <div className="flex items-center gap-2">
                                                <button
                                                    onClick={() => setCurrentPage(page => Math.max(1, page - 1))}
                                                    disabled={currentPage === 1}
                                                    className="p-1 rounded hover:bg-muted disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                                                >
                                                    <ArrowLeft className="w-4 h-4" />
                                                </button>
                                                <button
                                                    onClick={() => setCurrentPage(page => Math.min(totalPages, page + 1))}
                                                    disabled={currentPage === totalPages}
                                                    className="p-1 rounded hover:bg-muted disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                                                >
                                                    <ArrowRight className="w-4 h-4" />
                                                </button>
                                            </div>
                                        </div>
                                    )}
                                </div>
                            </>
                        )}
                    </main>
                </div>
            </div>

            <Dialog open={isDialogOpen} onOpenChange={setIsDialogOpen}>
                <DialogContent className="max-w-lg">
                    <DialogHeader>
                        <DialogTitle>{editingLocation ? "Editar Localização" : "Nova Localização"}</DialogTitle>
                        <DialogDescription>
                            A localização representa o local onde uma ou mais máquinas estão instaladas.
                        </DialogDescription>
                    </DialogHeader>
                    <div className="space-y-4 py-2">
                        <div className="space-y-2">
                            <label className="text-sm font-medium text-foreground">Nome da localização</label>
                            <Input
                                value={locationName}
                                onChange={(event) => setLocationName(event.target.value)}
                                placeholder="Ex: ACME - Shopping"
                                autoFocus
                            />
                        </div>
                        <div className="flex justify-end gap-2">
                            <Button variant="outline" onClick={() => setIsDialogOpen(false)}>
                                Cancelar
                            </Button>
                            <Button onClick={handleSave}>
                                Salvar
                            </Button>
                        </div>
                    </div>
                </DialogContent>
            </Dialog>
        </SidebarProvider>
    );
}
