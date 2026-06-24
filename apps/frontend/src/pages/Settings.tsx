import { SidebarProvider, SidebarTrigger } from "@/components/ui/sidebar";
import { AppSidebar } from "@/components/AppSidebar";
import { LogOut, Plus, Pencil, Trash2 } from "lucide-react";
import { useState, useEffect } from "react";
import { useNavigate } from "react-router-dom";
import { useAuth } from "@/contexts/AuthContext";
import { type ProductType } from "@/data/mockData";
import { getProductTypes } from "@/lib/api";
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
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from "@/components/ui/card";
import { useToast } from "@/components/ui/use-toast";

export default function Settings() {
    const navigate = useNavigate();
    const { logout, user } = useAuth();
    const { toast } = useToast();
    const [types, setTypes] = useState<ProductType[]>([]);
    const [loading, setLoading] = useState(true);



    useEffect(() => {
        if (sessionStorage.getItem("isAuthenticated") !== "true") {
            navigate("/");
            return;
        }

        setLoading(true);
        getProductTypes()
            .then(data => {
                setTypes(data);
                setLoading(false);
            })
            .catch(err => {
                console.error("Erro ao buscar tipos de produtos:", err);
                setLoading(false);
            });
    }, [navigate]);

    const [isDialogOpen, setIsDialogOpen] = useState(false);
    const [editingType, setEditingType] = useState<ProductType | null>(null);
    const [newTypeName, setNewTypeName] = useState("");

    const handleLogout = () => {
        logout();
    };

    const handleSaveType = () => {
        if (!newTypeName.trim()) {
            toast({
                title: "Erro",
                description: "O nome do tipo de produto não pode estar vazio.",
                variant: "destructive",
            });
            return;
        }

        if (editingType) {
            setTypes(types.map(t => t.id === editingType.id ? { ...t, name: newTypeName } : t));
            toast({
                title: "Sucesso",
                description: "Tipo de produto atualizado com sucesso.",
            });
        } else {
            const newId = `pt-${Date.now()}`;
            setTypes([...types, { id: newId, name: newTypeName }]);
            toast({
                title: "Sucesso",
                description: "Novo tipo de produto adicionado com sucesso.",
            });
        }
        
        setIsDialogOpen(false);
        setEditingType(null);
        setNewTypeName("");
    };

    const handleEdit = (type: ProductType) => {
        setEditingType(type);
        setNewTypeName(type.name);
        setIsDialogOpen(true);
    };

    const handleDelete = (id: string) => {
        if (confirm("Tem certeza que deseja excluir este tipo de produto?")) {
            setTypes(types.filter(t => t.id !== id));
            toast({
                title: "Sucesso",
                description: "Tipo de produto excluído com sucesso.",
            });
        }
    };

    const openNewDialog = () => {
        setEditingType(null);
        setNewTypeName("");
        setIsDialogOpen(true);
    };

    return (
        <SidebarProvider>
            <div className="min-h-screen flex w-full">
                <AppSidebar />
                <div className="flex-1 flex flex-col min-w-0">
                    <header className="h-12 flex items-center border-b bg-card px-4 gap-3">
                        <SidebarTrigger />
                        <h1 className="text-sm font-semibold text-foreground">Configurações</h1>
                        <div className="ml-auto flex items-center gap-4">
                            <span className="text-xs text-muted-foreground">
                                Última atualização: agora
                            </span>
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
                        {loading ? (
                            <div className="flex items-center justify-center h-[50vh]">
                                <p className="text-muted-foreground animate-pulse font-medium">Carregando configurações...</p>
                            </div>
                        ) : (
                            <>
                                <div className="flex items-center justify-between pb-4 border-b">
                            <div>
                                <h2 className="text-2xl font-bold tracking-tight">Configurações do Sistema</h2>
                                <p className="text-muted-foreground">
                                    Gerencie os parâmetros e dados base do sistema.
                                </p>
                            </div>
                        </div>

                        <div className="grid gap-6">
                            <Card>
                                <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-4 border-b">
                                    <div>
                                        <CardTitle className="text-lg">Tipos de Produto</CardTitle>
                                        <CardDescription>
                                            Cadastre e gerencie as categorias de produtos que serão usados nas máquinas.
                                        </CardDescription>
                                    </div>
                                    <Button onClick={openNewDialog} size="sm" className="flex items-center gap-2">
                                        <Plus className="w-4 h-4" />
                                        Novo Tipo
                                    </Button>
                                </CardHeader>
                                <CardContent className="p-0">
                                    <Table>
                                        <TableHeader>
                                            <TableRow>
                                                <TableHead className="px-6">Nome do Tipo</TableHead>
                                                <TableHead className="w-[100px] text-right px-6">Ações</TableHead>
                                            </TableRow>
                                        </TableHeader>
                                        <TableBody>
                                            {types.length === 0 ? (
                                                <TableRow>
                                                    <TableCell colSpan={2} className="text-center py-6 text-muted-foreground">
                                                        Nenhum tipo de produto cadastrado.
                                                    </TableCell>
                                                </TableRow>
                                            ) : (
                                                types.map((type) => (
                                                    <TableRow key={type.id}>
                                                        <TableCell className="font-medium px-6">{type.name}</TableCell>
                                                        <TableCell className="text-right px-6">
                                                            <div className="flex justify-end gap-2">
                                                                <Button
                                                                    variant="ghost"
                                                                    size="icon"
                                                                    onClick={() => handleEdit(type)}
                                                                >
                                                                    <Pencil className="w-4 h-4" />
                                                                </Button>
                                                                <Button
                                                                    variant="ghost"
                                                                    size="icon"
                                                                    className="text-destructive hover:text-destructive"
                                                                    onClick={() => handleDelete(type.id)}
                                                                >
                                                                    <Trash2 className="w-4 h-4" />
                                                                </Button>
                                                            </div>
                                                        </TableCell>
                                                    </TableRow>
                                                ))
                                            )}
                                        </TableBody>
                                    </Table>
                                </CardContent>
                            </Card>

                        </div>
                        
                        <Dialog open={isDialogOpen} onOpenChange={setIsDialogOpen}>
                            <DialogContent>
                                <DialogHeader>
                                    <DialogTitle>
                                        {editingType ? "Editar Tipo de Produto" : "Novo Tipo de Produto"}
                                    </DialogTitle>
                                </DialogHeader>
                                <div className="py-4">
                                    <label htmlFor="name" className="text-sm font-medium mb-2 block">
                                        Nome do Tipo <span className="text-destructive">*</span>
                                    </label>
                                    <Input
                                        id="name"
                                        placeholder="Ex: Bebidas Frias"
                                        value={newTypeName}
                                        onChange={(e) => setNewTypeName(e.target.value)}
                                        onKeyDown={(e) => {
                                            if (e.key === 'Enter') {
                                                handleSaveType();
                                            }
                                        }}
                                        autoFocus
                                    />
                                </div>
                                <DialogFooter>
                                    <Button variant="outline" onClick={() => setIsDialogOpen(false)}>
                                        Cancelar
                                    </Button>
                                    <Button onClick={handleSaveType}>
                                        Salvar
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
