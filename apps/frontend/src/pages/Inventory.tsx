import { useCallback, useEffect, useMemo, useState } from "react";
import { Boxes, PackagePlus, Pencil, RefreshCw, SlidersHorizontal } from "lucide-react";
import { SidebarProvider, SidebarTrigger } from "@/components/ui/sidebar";
import { AppSidebar } from "@/components/AppSidebar";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { useAuth } from "@/contexts/AuthContext";
import { useImpersonation } from "@/contexts/ImpersonationContext";
import {
    adjustInventorySlot,
    configureInventorySlot,
    getInventoryMovements,
    getInventorySlots,
    getMachines,
    getProducts,
    restockInventorySlot,
    type InventoryMovementItem,
    type InventorySlot
} from "@/lib/api";
import { type Machine, type FullProduct } from "@/data/mockData";
import { getErrorMessage } from "@/lib/utils";
import { toast } from "sonner";
import { useSearchParams } from "react-router-dom";

type SlotForm = {
    machineId: string;
    itemNumber: string;
    productId: string;
    capacity: string;
    initialStock: string;
    lowStockThreshold: string;
};

const emptyForm: SlotForm = {
    machineId: "",
    itemNumber: "",
    productId: "",
    capacity: "",
    initialStock: "0",
    lowStockThreshold: "0"
};

export default function Inventory() {
    const [searchParams, setSearchParams] = useSearchParams();
    const selectedMachineId = searchParams.get("machineId") ?? "all";
    const { user } = useAuth();
    const { isSuperAdmin } = useImpersonation();
    const canManage = user?.role === "Admin" || isSuperAdmin;
    const [machines, setMachines] = useState<Machine[]>([]);
    const [products, setProducts] = useState<FullProduct[]>([]);
    const [slots, setSlots] = useState<InventorySlot[]>([]);
    const [movements, setMovements] = useState<InventoryMovementItem[]>([]);
    const [loading, setLoading] = useState(true);
    const [hasLoaded, setHasLoaded] = useState(false);
    const [loadError, setLoadError] = useState(false);
    const [saving, setSaving] = useState(false);
    const [slotDialogOpen, setSlotDialogOpen] = useState(false);
    const [movementDialogOpen, setMovementDialogOpen] = useState(false);
    const [editingSlot, setEditingSlot] = useState<InventorySlot | null>(null);
    const [movementSlot, setMovementSlot] = useState<InventorySlot | null>(null);
    const [movementType, setMovementType] = useState<"Restock" | "Adjustment">("Restock");
    const [form, setForm] = useState<SlotForm>(emptyForm);
    const [quantity, setQuantity] = useState("1");
    const [reason, setReason] = useState("");

    const loadData = useCallback(async () => {
        setLoading(true);
        setLoadError(false);
        try {
            const [machineList, productList, slotList, movementList] = await Promise.all([
                getMachines(), getProducts(), getInventorySlots(), getInventoryMovements()
            ]);
            setMachines(machineList);
            setProducts(productList);
            setSlots(slotList);
            setMovements(movementList.items);
            setHasLoaded(true);
        } catch (error: unknown) {
            setLoadError(true);
            toast.error(getErrorMessage(error, "Não foi possível carregar o estoque."));
        } finally {
            setLoading(false);
        }
    }, []);

    useEffect(() => { void loadData(); }, [loadData]);

    const filteredSlots = useMemo(() => selectedMachineId === "all" ? slots : slots.filter(slot => slot.machineId === selectedMachineId), [slots, selectedMachineId]);
    const lowStockCount = useMemo(() => filteredSlots.filter(slot => slot.isLowStock).length, [filteredSlots]);
    const totalUnits = useMemo(() => filteredSlots.reduce((sum, slot) => sum + slot.currentStock, 0), [filteredSlots]);

    const openNewSlot = () => {
        setEditingSlot(null);
        setForm({ ...emptyForm, machineId: machines[0]?.id ?? "", productId: products[0]?.id ?? "" });
        setSlotDialogOpen(true);
    };

    const openEditSlot = (slot: InventorySlot) => {
        setEditingSlot(slot);
        setForm({
            machineId: slot.machineId,
            itemNumber: String(slot.itemNumber),
            productId: slot.productId,
            capacity: String(slot.capacity),
            initialStock: String(slot.currentStock),
            lowStockThreshold: String(slot.lowStockThreshold)
        });
        setSlotDialogOpen(true);
    };

    const saveSlot = async () => {
        const machineId = form.machineId;
        const itemNumber = Number(form.itemNumber);
        const capacity = Number(form.capacity);
        const initialStock = Number(form.initialStock);
        const lowStockThreshold = Number(form.lowStockThreshold);
        if (!machineId || !form.productId || !Number.isInteger(itemNumber) || itemNumber < 0 ||
            !Number.isInteger(capacity) || capacity < 1 || !Number.isInteger(initialStock) ||
            !Number.isInteger(lowStockThreshold)) {
            toast.error("Confira máquina, posição, produto, capacidade e saldo.");
            return;
        }

        setSaving(true);
        try {
            await configureInventorySlot(machineId, itemNumber, {
                productId: form.productId, capacity, initialStock, lowStockThreshold
            });
            toast.success(editingSlot ? "Compartimento atualizado." : "Compartimento cadastrado.");
            setSlotDialogOpen(false);
            await loadData();
        } catch (error: unknown) {
            toast.error(getErrorMessage(error, "Não foi possível salvar o compartimento."));
        } finally {
            setSaving(false);
        }
    };

    const openMovement = (slot: InventorySlot, type: "Restock" | "Adjustment") => {
        setMovementSlot(slot);
        setMovementType(type);
        setQuantity("1");
        setReason("");
        setMovementDialogOpen(true);
    };

    const saveMovement = async () => {
        if (!movementSlot) return;
        const amount = Number(quantity);
        if (!Number.isInteger(amount) || amount === 0 || (movementType === "Restock" && amount < 1) || !reason.trim()) {
            toast.error("Informe uma quantidade válida e o motivo da movimentação.");
            return;
        }

        setSaving(true);
        try {
            if (movementType === "Restock") {
                await restockInventorySlot(movementSlot.machineId, movementSlot.itemNumber, amount, reason.trim());
                toast.success("Reposição registrada.");
            } else {
                await adjustInventorySlot(movementSlot.machineId, movementSlot.itemNumber, amount, reason.trim());
                toast.success("Ajuste registrado.");
            }
            setMovementDialogOpen(false);
            await loadData();
        } catch (error: unknown) {
            toast.error(getErrorMessage(error, "Não foi possível registrar a movimentação."));
        } finally {
            setSaving(false);
        }
    };

    return (
        <SidebarProvider>
            <div className="min-h-screen flex w-full">
                <AppSidebar />
                <main className="flex-1 min-w-0">
                    <header className="h-12 flex items-center border-b bg-card px-4 gap-3">
                        <SidebarTrigger />
                        <h1 className="text-sm font-semibold">Estoque</h1>
                        <Button variant="ghost" size="icon" className="ml-auto" onClick={() => void loadData()} disabled={loading} aria-label="Atualizar estoque">
                            <RefreshCw className={`h-4 w-4 ${loading ? "animate-spin" : ""}`} />
                        </Button>
                    </header>
                    <div className="p-4 md:p-8 max-w-7xl mx-auto space-y-6">
                        <div className="flex flex-col sm:flex-row sm:items-end justify-between gap-4">
                            <div>
                                <p className="text-xs uppercase tracking-widest text-muted-foreground">Operação</p>
                                <h2 className="text-2xl font-bold mt-1">Compartimentos e reposição</h2>
                                <p className="text-sm text-muted-foreground mt-1">Associe as posições MDB aos produtos e registre cada movimento.</p>
                            </div>
                            {canManage && <Button onClick={openNewSlot} disabled={!machines.length || !products.length}><Boxes className="h-4 w-4 mr-2" />Cadastrar compartimento</Button>}
                        </div>

                        {loadError && (
                            <div role="alert" className="flex flex-wrap items-center justify-between gap-3 rounded-md border border-destructive/30 bg-destructive/5 p-3 text-sm">
                                <span>{hasLoaded ? "Não foi possível atualizar o estoque. Os dados exibidos podem estar desatualizados." : "Não foi possível carregar o estoque. Confira sua conexão e tente novamente."}</span>
                                <Button variant="outline" size="sm" onClick={() => void loadData()} disabled={loading}>
                                    Tentar novamente
                                </Button>
                            </div>
                        )}

                        {hasLoaded && <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
                            <div className="rounded-lg border bg-card p-4"><p className="text-sm text-muted-foreground">Compartimentos cadastrados</p><p className="text-2xl font-semibold mt-1">{slots.length}</p></div>
                            <div className="rounded-lg border bg-card p-4"><p className="text-sm text-muted-foreground">Unidades registradas</p><p className="text-2xl font-semibold mt-1">{totalUnits}</p></div>
                            <div className="rounded-lg border bg-card p-4"><p className="text-sm text-muted-foreground">Abaixo do limite</p><p className="text-2xl font-semibold mt-1">{lowStockCount}</p></div>
                        </div>}

                        <section className="rounded-lg border bg-card">
                            <div className="p-4 border-b flex flex-col sm:flex-row sm:items-center justify-between gap-3"><div><h3 className="font-semibold">Estoque por máquina</h3><p className="text-xs text-muted-foreground mt-1">Saldo atualizado por vendas confirmadas e movimentações registradas.</p></div><select aria-label="Filtrar estoque por máquina" className="h-10 rounded-md border bg-background px-3 text-sm" value={selectedMachineId} onChange={event => { const value = event.target.value; setSearchParams(value === "all" ? {} : { machineId: value }); }}><option value="all">Todas as máquinas</option>{machines.map(machine => <option key={machine.id} value={machine.id}>{machine.name} · {machine.id}</option>)}</select></div>
                            <div className="overflow-x-auto">
                                <Table>
                                    <TableHeader><TableRow><TableHead>Máquina</TableHead><TableHead>Posição</TableHead><TableHead>Produto</TableHead><TableHead>Saldo</TableHead><TableHead>Capacidade</TableHead><TableHead>Limite</TableHead><TableHead>Estado</TableHead><TableHead className="text-right">Ações</TableHead></TableRow></TableHeader>
                                    <TableBody>
                                        {loading && !hasLoaded ? <TableRow><TableCell colSpan={8} className="text-center py-10 text-muted-foreground">Carregando estoque...</TableCell></TableRow> : loadError && !hasLoaded ? <TableRow><TableCell colSpan={8} className="text-center py-10 text-destructive">Dados do estoque indisponíveis.</TableCell></TableRow> : filteredSlots.length === 0 ? <TableRow><TableCell colSpan={8} className="text-center py-10 text-muted-foreground">{selectedMachineId === "all" ? "Nenhum compartimento cadastrado para esta empresa." : "Esta máquina ainda não tem compartimentos cadastrados."}</TableCell></TableRow> : filteredSlots.map(slot => (
                                            <TableRow key={slot.id}>
                                                <TableCell className="font-medium">{slot.machineName || slot.machineId}</TableCell>
                                                <TableCell>{slot.itemNumber}</TableCell>
                                                <TableCell>{slot.productName || "Produto sem nome"}</TableCell>
                                                <TableCell>{slot.currentStock} un.</TableCell>
                                                <TableCell>{slot.capacity} un.</TableCell>
                                                <TableCell>{slot.lowStockThreshold} un.</TableCell>
                                                <TableCell><span className={`text-xs rounded-full px-2 py-1 ${slot.isLowStock ? "bg-amber-100 text-amber-800" : "bg-emerald-100 text-emerald-800"}`}>{slot.isLowStock ? "Repor" : "Normal"}</span></TableCell>
                                                <TableCell className="text-right whitespace-nowrap">
                                                    {canManage && <>
                                                        <Button variant="ghost" size="sm" onClick={() => openMovement(slot, "Restock")} aria-label={`Repor ${slot.productName || slot.itemNumber}`}><PackagePlus className="h-4 w-4 mr-1" />Repor</Button>
                                                        <Button variant="ghost" size="sm" onClick={() => openMovement(slot, "Adjustment")} aria-label={`Ajustar ${slot.productName || slot.itemNumber}`}><SlidersHorizontal className="h-4 w-4 mr-1" />Ajustar</Button>
                                                        <Button variant="ghost" size="icon" onClick={() => openEditSlot(slot)} aria-label="Editar compartimento"><Pencil className="h-4 w-4" /></Button>
                                                    </>}
                                                </TableCell>
                                            </TableRow>
                                        ))}
                                    </TableBody>
                                </Table>
                            </div>
                        </section>

                        <section className="rounded-lg border bg-card">
                            <div className="p-4 border-b"><h3 className="font-semibold">Movimentações recentes</h3><p className="text-xs text-muted-foreground mt-1">Reposições, ajustes e saídas por venda.</p></div>
                            <div className="overflow-x-auto">
                                <Table>
                                    <TableHeader><TableRow><TableHead>Data</TableHead><TableHead>Máquina</TableHead><TableHead>Posição</TableHead><TableHead>Produto</TableHead><TableHead>Movimento</TableHead><TableHead>Variação</TableHead><TableHead>Saldo após</TableHead><TableHead>Motivo</TableHead></TableRow></TableHeader>
                                    <TableBody>
                                        {loading && !hasLoaded ? <TableRow><TableCell colSpan={8} className="text-center py-8 text-muted-foreground">Carregando movimentações...</TableCell></TableRow> : loadError && !hasLoaded ? <TableRow><TableCell colSpan={8} className="text-center py-8 text-destructive">Movimentações indisponíveis.</TableCell></TableRow> : movements.length === 0 ? <TableRow><TableCell colSpan={8} className="text-center py-8 text-muted-foreground">Ainda não há movimentações registradas.</TableCell></TableRow> : movements.map(movement => (
                                            <TableRow key={movement.id}>
                                                <TableCell>{new Date(movement.createdAt).toLocaleString("pt-BR")}</TableCell>
                                                <TableCell>{movement.machineId}</TableCell>
                                                <TableCell>{movement.itemNumber}</TableCell>
                                                <TableCell>{movement.productName || "Produto removido"}</TableCell>
                                                <TableCell>{movement.movementType === "Sale" ? "Venda" : movement.movementType === "Restock" ? "Reposição" : movement.movementType === "OpeningBalance" ? "Saldo inicial" : "Ajuste"}</TableCell>
                                                <TableCell className={movement.quantityDelta < 0 ? "text-destructive" : "text-emerald-700"}>{movement.quantityDelta > 0 ? "+" : ""}{movement.quantityDelta}</TableCell>
                                                <TableCell>{movement.quantityAfter}</TableCell>
                                                <TableCell className="max-w-xs truncate" title={movement.reason}>{movement.reason}</TableCell>
                                            </TableRow>
                                        ))}
                                    </TableBody>
                                </Table>
                            </div>
                        </section>
                    </div>
                </main>
            </div>

            <Dialog open={slotDialogOpen} onOpenChange={setSlotDialogOpen}>
                <DialogContent>
                    <DialogHeader><DialogTitle>{editingSlot ? "Editar compartimento" : "Cadastrar compartimento"}</DialogTitle></DialogHeader>
                    <div className="grid gap-4 py-2">
                        <div className="grid gap-2"><Label htmlFor="slot-machine">Máquina</Label><select id="slot-machine" className="h-10 rounded-md border bg-background px-3 text-sm" value={form.machineId} disabled={!!editingSlot} onChange={event => setForm({ ...form, machineId: event.target.value })}><option value="">Selecione</option>{machines.map(machine => <option key={machine.id} value={machine.id}>{machine.name} · {machine.id}</option>)}</select></div>
                        <div className="grid gap-2"><Label htmlFor="slot-number">Posição MDB</Label><Input id="slot-number" type="number" min="0" max="9999" value={form.itemNumber} disabled={!!editingSlot} onChange={event => setForm({ ...form, itemNumber: event.target.value })} /></div>
                        <div className="grid gap-2"><Label htmlFor="slot-product">Produto</Label><select id="slot-product" className="h-10 rounded-md border bg-background px-3 text-sm" value={form.productId} onChange={event => setForm({ ...form, productId: event.target.value })}><option value="">Selecione</option>{products.map(product => <option key={product.id} value={product.id}>{product.name} · {product.code}</option>)}</select></div>
                        <div className="grid grid-cols-3 gap-3">
                            <div className="grid gap-2"><Label htmlFor="slot-capacity">Capacidade</Label><Input id="slot-capacity" type="number" min="1" max="100000" value={form.capacity} onChange={event => setForm({ ...form, capacity: event.target.value })} /></div>
                            <div className="grid gap-2"><Label htmlFor="slot-stock">Saldo atual</Label><Input id="slot-stock" type="number" min="0" value={form.initialStock} disabled={!!editingSlot} onChange={event => setForm({ ...form, initialStock: event.target.value })} /></div>
                            <div className="grid gap-2"><Label htmlFor="slot-low-stock">Limite baixo</Label><Input id="slot-low-stock" type="number" min="0" value={form.lowStockThreshold} onChange={event => setForm({ ...form, lowStockThreshold: event.target.value })} /></div>
                        </div>
                        {editingSlot && <p className="text-xs text-muted-foreground">Para alterar o saldo, use Repor ou Ajustar. Assim o histórico permanece íntegro.</p>}
                    </div>
                    <DialogFooter><Button variant="outline" onClick={() => setSlotDialogOpen(false)}>Cancelar</Button><Button onClick={() => void saveSlot()} disabled={saving}>{saving ? "Salvando..." : "Salvar"}</Button></DialogFooter>
                </DialogContent>
            </Dialog>

            <Dialog open={movementDialogOpen} onOpenChange={setMovementDialogOpen}>
                <DialogContent>
                    <DialogHeader><DialogTitle>{movementType === "Restock" ? "Registrar reposição" : "Ajustar saldo"}</DialogTitle></DialogHeader>
                    <p className="text-sm text-muted-foreground">{movementSlot?.productName || "Produto"} · {movementSlot?.machineName || movementSlot?.machineId} · posição {movementSlot?.itemNumber}</p>
                    <div className="grid gap-4 py-2">
                        <div className="grid gap-2"><Label htmlFor="movement-quantity">{movementType === "Restock" ? "Unidades adicionadas" : "Variação (use negativo para retirar)"}</Label><Input id="movement-quantity" type="number" step="1" value={quantity} onChange={event => setQuantity(event.target.value)} /></div>
                        <div className="grid gap-2"><Label htmlFor="movement-reason">Motivo</Label><Input id="movement-reason" maxLength={500} value={reason} onChange={event => setReason(event.target.value)} placeholder={movementType === "Restock" ? "Ex.: reposição semanal" : "Ex.: contagem física"} /></div>
                    </div>
                    <DialogFooter><Button variant="outline" onClick={() => setMovementDialogOpen(false)}>Cancelar</Button><Button onClick={() => void saveMovement()} disabled={saving}>{saving ? "Salvando..." : "Registrar"}</Button></DialogFooter>
                </DialogContent>
            </Dialog>
        </SidebarProvider>
    );
}
