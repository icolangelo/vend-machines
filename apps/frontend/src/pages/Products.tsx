import { SidebarProvider, SidebarTrigger } from "@/components/ui/sidebar";
import { AppSidebar } from "@/components/AppSidebar";
import { LogOut, Plus, Pencil, Trash2, Package, Wine, DollarSign, Tag, RefreshCw } from "lucide-react";
import { useState, useEffect, useCallback } from "react";
import { useAuth } from "@/contexts/AuthContext";
import { type FullProduct, type ProductType } from "@/data/mockData";
import { getProducts, getProductTypes, createProduct, updateProduct, deleteProduct } from "@/lib/api";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
    Table,
    TableBody,
    TableCell,
    TableHead,
    TableHeader,
    TableRow,
} from "@/components/ui/table";
import {
    Dialog,
    DialogContent,
    DialogHeader,
    DialogTitle,
    DialogFooter,
} from "@/components/ui/dialog";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { useToast } from "@/components/ui/use-toast";
import {
    Select,
    SelectContent,
    SelectItem,
    SelectTrigger,
    SelectValue,
} from "@/components/ui/select";
import { Label } from "@/components/ui/label";
import { Checkbox } from "@/components/ui/checkbox";
import { Textarea } from "@/components/ui/textarea";
import { getErrorMessage } from "@/lib/utils";

export default function Products() {
    const { logout, user } = useAuth();
    const canManageProducts = user?.role === "Admin" || user?.role === "PlatformAdmin";
    const { toast } = useToast();
    const [products, setProducts] = useState<FullProduct[]>([]);
    const [productTypes, setProductTypes] = useState<ProductType[]>([]);
    const [loading, setLoading] = useState(true);
    const [hasLoaded, setHasLoaded] = useState(false);
    const [loadError, setLoadError] = useState(false);
    const [lastLoadedAt, setLastLoadedAt] = useState<Date | null>(null);

    const loadProducts = useCallback(async () => {
        setLoading(true);
        setLoadError(false);
        try {
            const [pList, ptList] = await Promise.all([getProducts(), getProductTypes()]);
            setProducts(pList);
            setProductTypes(ptList);
            setHasLoaded(true);
            setLastLoadedAt(new Date());
        } catch (err) {
            console.error("Erro ao carregar produtos:", err);
            setLoadError(true);
            toast({
                title: "Não foi possível carregar os produtos",
                description: "Confira sua conexão e tente novamente.",
                variant: "destructive",
            });
        } finally {
            setLoading(false);
        }
    }, [toast]);

    useEffect(() => { void loadProducts(); }, [loadProducts]);

    const [isDialogOpen, setIsDialogOpen] = useState(false);
    const [editingProduct, setEditingProduct] = useState<FullProduct | null>(null);
    
    // Form state
    const [formData, setFormData] = useState<Partial<FullProduct>>({
        code: "",
        name: "",
        description: "",
        typeId: "",
        isAlcoholic: false,
        cost: 0,
    });

    const handleLogout = () => {
        logout();
    };

    const handleSaveProduct = async () => {
        if (!canManageProducts) return;
        if (!formData.code || !formData.name || !formData.typeId) {
            toast({
                title: "Erro",
                description: "Por favor, preencha todos os campos obrigatórios.",
                variant: "destructive",
            });
            return;
        }

        try {
            if (editingProduct) {
                const updatedProduct = await updateProduct(editingProduct.id, formData);
                setProducts(products.map(p => p.id === editingProduct.id ? updatedProduct : p));
                toast({
                    title: "Sucesso",
                    description: "Produto atualizado com sucesso.",
                });
            } else {
                const newProduct = await createProduct(formData);
                setProducts([...products, newProduct]);
                toast({
                    title: "Sucesso",
                    description: "Novo produto adicionado com sucesso.",
                });
            }
            
            setIsDialogOpen(false);
            setEditingProduct(null);
            setFormData({
                code: "",
                name: "",
                description: "",
                typeId: "",
                isAlcoholic: false,
                cost: 0,
            });
        } catch (err: unknown) {
            toast({
                title: "Erro",
                description: getErrorMessage(err, "Erro ao salvar produto."),
                variant: "destructive",
            });
        }
    };

    const handleEdit = (product: FullProduct) => {
        if (!canManageProducts) return;
        setEditingProduct(product);
        setFormData(product);
        setIsDialogOpen(true);
    };

    const handleDelete = async (id: string) => {
        if (!canManageProducts) return;
        if (confirm("Tem certeza que deseja excluir este produto?")) {
            try {
                await deleteProduct(id);
                setProducts(products.filter(p => p.id !== id));
                toast({
                    title: "Sucesso",
                    description: "Produto excluído com sucesso.",
                });
            } catch (err: unknown) {
                toast({
                    title: "Erro",
                    description: getErrorMessage(err, "Erro ao excluir produto."),
                    variant: "destructive",
                });
            }
        }
    };

    const openNewDialog = () => {
        setEditingProduct(null);
        setFormData({
            code: "",
            name: "",
            description: "",
            typeId: "",
            isAlcoholic: false,
            cost: 0,
        });
        setIsDialogOpen(true);
    };

    const totalProducts = products.length;
    const alcoholicProducts = products.filter(p => p.isAlcoholic).length;
    const avgCost = products.length > 0 ? products.reduce((acc, p) => acc + p.cost, 0) / products.length : 0;

    return (
        <SidebarProvider>
            <div className="min-h-screen flex w-full">
                <AppSidebar />
                <div className="flex-1 flex flex-col min-w-0">
                    <header className="h-12 flex items-center border-b bg-card px-4 gap-3">
                        <SidebarTrigger />
                        <h1 className="text-sm font-semibold text-foreground">Produtos</h1>
                        <div className="ml-auto flex items-center gap-4">
                            <span className="text-xs text-muted-foreground">
                                {lastLoadedAt ? `Atualizado às ${lastLoadedAt.toLocaleTimeString("pt-BR")}` : "Catálogo de produtos"}
                            </span>
                            <Button variant="ghost" size="icon" onClick={() => void loadProducts()} disabled={loading} aria-label="Atualizar produtos">
                                <RefreshCw className={`h-4 w-4 ${loading ? "animate-spin" : ""}`} />
                            </Button>
                            <button
                                onClick={handleLogout}
                                className="text-muted-foreground hover:text-foreground transition-colors flex items-center gap-2"
                                title="Sair"
                            >
                                <LogOut className="w-4 h-4" />
                            </button>
                        </div>
                    </header>

                    <main className="flex-1 p-6 space-y-6 overflow-auto">
                        {loading && !hasLoaded ? (
                            <div className="flex items-center justify-center h-[50vh]">
                                <p className="text-muted-foreground animate-pulse font-medium">Carregando produtos...</p>
                            </div>
                        ) : loadError && !hasLoaded ? (
                            <div role="alert" className="flex min-h-[40vh] flex-col items-center justify-center gap-3 rounded-lg border border-dashed bg-card p-6 text-center">
                                <p className="font-medium">Não foi possível carregar o catálogo de produtos.</p>
                                <p className="text-sm text-muted-foreground">Confira sua conexão e tente novamente.</p>
                                <Button variant="outline" onClick={() => void loadProducts()} disabled={loading}>Tentar novamente</Button>
                            </div>
                        ) : (
                            <>
                                {loadError && (
                                    <div role="alert" className="flex flex-wrap items-center justify-between gap-3 rounded-md border border-destructive/30 bg-destructive/5 p-3 text-sm">
                                        <span>Não foi possível atualizar o catálogo. Os dados exibidos podem estar desatualizados.</span>
                                        <Button variant="outline" size="sm" onClick={() => void loadProducts()} disabled={loading}>Tentar novamente</Button>
                                    </div>
                                )}
                                <div className="flex items-center justify-between pb-4 border-b">
                            <div>
                                <h2 className="text-2xl font-bold tracking-tight">Gerenciamento de Produtos</h2>
                                <p className="text-muted-foreground">
                                    Cadastre e gerencie o catálogo de produtos das suas máquinas.
                                </p>
                            </div>
                            {canManageProducts && <Button onClick={openNewDialog} className="flex items-center gap-2">
                                <Plus className="w-4 h-4" />
                                Novo Produto
                            </Button>}
                        </div>

                        <div className="grid gap-4 md:grid-cols-3">
                            <Card className="bg-card/50 backdrop-blur-sm border-primary/10">
                                <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
                                    <CardTitle className="text-sm font-medium">Total de Produtos</CardTitle>
                                    <Package className="h-4 w-4 text-primary" />
                                </CardHeader>
                                <CardContent>
                                    <div className="text-2xl font-bold">{totalProducts}</div>
                                    <p className="text-xs text-muted-foreground">Cadastrados no sistema</p>
                                </CardContent>
                            </Card>
                            <Card className="bg-card/50 backdrop-blur-sm border-primary/10">
                                <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
                                    <CardTitle className="text-sm font-medium">Produtos Alcoólicos</CardTitle>
                                    <Wine className="h-4 w-4 text-orange-500" />
                                </CardHeader>
                                <CardContent>
                                    <div className="text-2xl font-bold">{alcoholicProducts}</div>
                                    <p className="text-xs text-muted-foreground">Requerem verificação de idade</p>
                                </CardContent>
                            </Card>
                            <Card className="bg-card/50 backdrop-blur-sm border-primary/10">
                                <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
                                    <CardTitle className="text-sm font-medium">Custo Médio</CardTitle>
                                    <DollarSign className="h-4 w-4 text-green-500" />
                                </CardHeader>
                                <CardContent>
                                    <div className="text-2xl font-bold">R$ {avgCost.toLocaleString('pt-BR', { minimumFractionDigits: 3, maximumFractionDigits: 3 })}</div>
                                    <p className="text-xs text-muted-foreground">Baseado no custo de entrada</p>
                                </CardContent>
                            </Card>
                        </div>

                        <Card>
                            <CardContent className="p-0">
                                <Table>
                                    <TableHeader>
                                        <TableRow>
                                            <TableHead className="px-6">Código</TableHead>
                                            <TableHead className="px-6">Nome</TableHead>
                                            <TableHead className="px-6">Tipo</TableHead>
                                            <TableHead className="px-6 text-center">Alcoólico</TableHead>
                                            <TableHead className="px-6 text-right">Custo (R$)</TableHead>
                                            <TableHead className="w-[100px] text-right px-6">Ações</TableHead>
                                        </TableRow>
                                    </TableHeader>
                                    <TableBody>
                                        {products.length === 0 ? (
                                            <TableRow>
                                                <TableCell colSpan={6} className="text-center py-10 text-muted-foreground">
                                                    Nenhum produto cadastrado.
                                                </TableCell>
                                            </TableRow>
                                        ) : (
                                            products.map((product) => {
                                                const typeName = productTypes.find(t => t.id === product.typeId)?.name || "N/A";
                                                return (
                                                    <TableRow key={product.id}>
                                                        <TableCell className="font-mono text-xs px-6">{product.code}</TableCell>
                                                        <TableCell className="font-medium px-6">{product.name}</TableCell>
                                                        <TableCell className="px-6">
                                                            <span className="inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium bg-secondary text-secondary-foreground">
                                                                {typeName}
                                                            </span>
                                                        </TableCell>
                                                        <TableCell className="text-center px-6">
                                                            {product.isAlcoholic ? (
                                                                <Wine className="h-4 w-4 text-orange-500 mx-auto" aria-label="Alcoólico" />
                                                            ) : (
                                                                <span className="text-muted-foreground/30">-</span>
                                                            )}
                                                        </TableCell>
                                                        <TableCell className="text-right px-6 font-mono">
                                                            {product.cost.toLocaleString('pt-BR', { minimumFractionDigits: 3, maximumFractionDigits: 3 })}
                                                        </TableCell>
                                                        <TableCell className="text-right px-6">
                                                            {canManageProducts && <div className="flex justify-end gap-2">
                                                                <Button
                                                                    variant="ghost"
                                                                    size="icon"
                                                                    onClick={() => handleEdit(product)}
                                                                >
                                                                    <Pencil className="w-4 h-4" />
                                                                </Button>
                                                                <Button
                                                                    variant="ghost"
                                                                    size="icon"
                                                                    className="text-destructive hover:text-destructive"
                                                                    onClick={() => handleDelete(product.id)}
                                                                >
                                                                    <Trash2 className="w-4 h-4" />
                                                                </Button>
                                                            </div>}
                                                        </TableCell>
                                                    </TableRow>
                                                );
                                            })
                                        )}
                                    </TableBody>
                                </Table>
                            </CardContent>
                        </Card>
                        
                        <Dialog open={isDialogOpen} onOpenChange={setIsDialogOpen}>
                            <DialogContent className="sm:max-w-[500px]">
                                <DialogHeader>
                                    <DialogTitle>
                                        {editingProduct ? "Editar Produto" : "Novo Cadastro de Produto"}
                                    </DialogTitle>
                                </DialogHeader>
                                <div className="grid gap-4 py-4">
                                    <div className="grid grid-cols-2 gap-4">
                                        <div className="space-y-2">
                                            <Label htmlFor="code">Código do Produto <span className="text-destructive">*</span></Label>
                                            <Input
                                                id="code"
                                                placeholder="Ex: PROD-001"
                                                value={formData.code}
                                                onChange={(e) => setFormData({ ...formData, code: e.target.value })}
                                            />
                                        </div>
                                        <div className="space-y-2">
                                            <Label htmlFor="name">Nome Simplificado <span className="text-destructive">*</span></Label>
                                            <Input
                                                id="name"
                                                placeholder="Ex: Coca-Cola Lata"
                                                value={formData.name}
                                                onChange={(e) => setFormData({ ...formData, name: e.target.value })}
                                            />
                                        </div>
                                    </div>
                                    
                                    <div className="space-y-2">
                                        <Label htmlFor="description">Descrição do Produto</Label>
                                        <Textarea
                                            id="description"
                                            placeholder="Descreva detalhes do produto..."
                                            value={formData.description}
                                            onChange={(e) => setFormData({ ...formData, description: e.target.value })}
                                            className="min-h-[80px]"
                                        />
                                    </div>

                                    <div className="grid grid-cols-2 gap-4">
                                        <div className="space-y-2">
                                            <Label htmlFor="type">Tipo de Produto <span className="text-destructive">*</span></Label>
                                            <Select 
                                                value={formData.typeId} 
                                                onValueChange={(value) => setFormData({ ...formData, typeId: value })}
                                            >
                                                <SelectTrigger id="type">
                                                    <SelectValue placeholder="Selecione um tipo" />
                                                </SelectTrigger>
                                                <SelectContent>
                                                    {productTypes.filter(t => t.id && t.id.trim() !== "").map((type) => (
                                                        <SelectItem key={type.id} value={type.id}>
                                                            {type.name}
                                                        </SelectItem>
                                                    ))}
                                                </SelectContent>
                                            </Select>
                                        </div>
                                        <div className="space-y-2">
                                            <Label htmlFor="cost">Custo de Produto (R$) <span className="text-destructive">*</span></Label>
                                            <Input
                                                id="cost"
                                                type="number"
                                                step="0.001"
                                                placeholder="0,000"
                                                value={formData.cost}
                                                onChange={(e) => setFormData({ ...formData, cost: parseFloat(e.target.value) })}
                                            />
                                            <p className="text-[10px] text-muted-foreground italic">* Valor com 3 casas decimais</p>
                                        </div>
                                    </div>

                                    <div className="flex items-center space-x-2 pt-2">
                                        <Checkbox 
                                            id="isAlcoholic" 
                                            checked={formData.isAlcoholic}
                                            onCheckedChange={(checked) => setFormData({ ...formData, isAlcoholic: checked === true })}
                                        />
                                        <Label 
                                            htmlFor="isAlcoholic"
                                            className="text-sm font-medium leading-none peer-disabled:cursor-not-allowed peer-disabled:opacity-70 flex items-center gap-2"
                                        >
                                            Este é um produto Alcoólico
                                            {formData.isAlcoholic && <Wine className="h-3 w-3 text-orange-500" />}
                                        </Label>
                                    </div>
                                </div>
                                <DialogFooter>
                                    <Button variant="outline" onClick={() => setIsDialogOpen(false)}>
                                        Cancelar
                                    </Button>
                                    <Button onClick={handleSaveProduct}>
                                        {editingProduct ? "Salvar Alterações" : "Cadastrar Produto"}
                                    </Button>
                                </DialogFooter>
                            </DialogContent>
                        </Dialog>
                    </>
                )}
            </main>
                </div>
            </div>
        </SidebarProvider>
    );
}
