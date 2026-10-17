import { useCallback, useEffect, useState, type FormEvent } from "react";
import { SidebarProvider, SidebarTrigger } from "@/components/ui/sidebar";
import { AppSidebar } from "@/components/AppSidebar";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { useAuth } from "@/contexts/AuthContext";
import { createPasswordResetLink, createUserInvitation, getUsers, revokeUserInvitation, updateUserActiveStatus, updateUserRole, type PaginatedUsers } from "@/lib/api";
import { ChevronLeft, ChevronRight, Copy, KeyRound, Search, ShieldAlert, UserPlus, Users as UsersIcon, X } from "lucide-react";
import { toast } from "sonner";

export default function Users() {
    const { user } = useAuth();
    const [data, setData] = useState<PaginatedUsers | null>(null);
    const [page, setPage] = useState(1);
    const [searchInput, setSearchInput] = useState("");
    const [search, setSearch] = useState("");
    const [loading, setLoading] = useState(true);
    const [changingUserId, setChangingUserId] = useState<string | null>(null);
    const [inviteOpen, setInviteOpen] = useState(false);
    const [inviteEmail, setInviteEmail] = useState("");
    const [inviteRole, setInviteRole] = useState<"Admin" | "Operator">("Operator");
    const [inviteLink, setInviteLink] = useState("");
    const [inviteEmailSent, setInviteEmailSent] = useState(false);
    const [inviteId, setInviteId] = useState("");
    const [inviteExpiresAt, setInviteExpiresAt] = useState("");
    const [creatingInvite, setCreatingInvite] = useState(false);
    const [resetOpen, setResetOpen] = useState(false);
    const [resetLink, setResetLink] = useState("");
    const [resetEmailSent, setResetEmailSent] = useState(false);
    const [resetExpiresAt, setResetExpiresAt] = useState("");
    const [resetTargetName, setResetTargetName] = useState("");
    const [resetTargetId, setResetTargetId] = useState("");
    const [creatingReset, setCreatingReset] = useState(false);

    const canManageUsers = user?.role === "Admin";

    const loadUsers = useCallback(async () => {
        setLoading(true);
        try {
            setData(await getUsers(page, 10, search));
        } catch (error) {
            toast.error(error instanceof Error ? error.message : "Não foi possível carregar os usuários.");
        } finally {
            setLoading(false);
        }
    }, [page, search]);

    useEffect(() => {
        if (canManageUsers) void loadUsers();
    }, [canManageUsers, loadUsers]);

    const submitSearch = (event: FormEvent<HTMLFormElement>) => {
        event.preventDefault();
        setPage(1);
        setSearch(searchInput.trim());
    };

    const changeRole = async (userId: string, role: "Admin" | "Operator") => {
        if (!window.confirm("Alterar o papel encerrará as sessões ativas desta pessoa. Deseja continuar?")) {
            return;
        }
        setChangingUserId(userId);
        try {
            await updateUserRole(userId, role);
            await loadUsers();
            toast.success("Papel atualizado. A pessoa precisará entrar novamente.");
        } catch (error) {
            toast.error(error instanceof Error ? error.message : "Não foi possível alterar o papel.");
        } finally {
            setChangingUserId(null);
        }
    };

    const changeActiveStatus = async (userId: string, isActive: boolean) => {
        const action = isActive ? "reativar" : "desativar";
        if (!window.confirm(`Deseja ${action} este usuário? ${isActive ? "Ele poderá entrar novamente." : "As sessões ativas serão encerradas."}`)) {
            return;
        }
        setChangingUserId(userId);
        try {
            await updateUserActiveStatus(userId, isActive);
            await loadUsers();
            toast.success(isActive ? "Usuário reativado." : "Usuário desativado e sessões encerradas.");
        } catch (error) {
            toast.error(error instanceof Error ? error.message : "Não foi possível atualizar o usuário.");
        } finally {
            setChangingUserId(null);
        }
    };

    const submitInvitation = async (event: FormEvent<HTMLFormElement>) => {
        event.preventDefault();
        setCreatingInvite(true);
        setInviteLink("");
        setInviteEmailSent(false);
        try {
            const invitation = await createUserInvitation(inviteEmail.trim(), inviteRole);
            setInviteId(invitation.invitationId);
            setInviteLink(invitation.invitationToken
                ? `${window.location.origin}/#/invite?token=${encodeURIComponent(invitation.invitationToken)}`
                : "");
            setInviteEmailSent(invitation.emailSent);
            setInviteExpiresAt(invitation.expiresAt);
            toast.success(invitation.emailSent
                ? "Convite enviado por e-mail."
                : invitation.rotated ? "Novo link criado; o link anterior foi invalidado." : "Convite criado.");
        } catch (error) {
            toast.error(error instanceof Error ? error.message : "Não foi possível criar o convite.");
        } finally {
            setCreatingInvite(false);
        }
    };

    const revokeInvitation = async () => {
        if (!inviteId || !window.confirm("Revogar este link agora? A pessoa não poderá mais aceitá-lo.")) return;
        try {
            await revokeUserInvitation(inviteId);
            setInviteLink("");
            setInviteEmailSent(false);
            setInviteId("");
            toast.success("Link de convite revogado.");
        } catch (error) {
            toast.error(error instanceof Error ? error.message : "Não foi possível revogar o convite.");
        }
    };

    const copyInvitationLink = async () => {
        try {
            await navigator.clipboard.writeText(inviteLink);
            toast.success("Link copiado. Compartilhe-o diretamente com o convidado.");
        } catch {
            toast.error("Não foi possível copiar automaticamente. Selecione e copie o link.");
        }
    };

    const createRecoveryLink = async (userId: string, name: string) => {
        if (!window.confirm(`Gerar um link de recuperação para ${name}? Qualquer link anterior será invalidado.`)) return;
        setCreatingReset(true);
        setResetEmailSent(false);
        try {
            const reset = await createPasswordResetLink(userId);
            setResetTargetId(userId);
            setResetTargetName(name);
            setResetLink(reset.resetToken
                ? `${window.location.origin}/#/reset-password?token=${encodeURIComponent(reset.resetToken)}`
                : "");
            setResetEmailSent(reset.emailSent);
            setResetExpiresAt(reset.expiresAt);
            setResetOpen(true);
            if (reset.emailSent) toast.success("Link de recuperação enviado ao usuário por e-mail.");
        } catch (error) {
            toast.error(error instanceof Error ? error.message : "Não foi possível gerar o link de recuperação.");
        } finally {
            setCreatingReset(false);
        }
    };

    const copyResetLink = async () => {
        try {
            await navigator.clipboard.writeText(resetLink);
            toast.success("Link copiado. Compartilhe-o diretamente com o usuário.");
        } catch {
            toast.error("Não foi possível copiar automaticamente. Selecione e copie o link.");
        }
    };

    if (!canManageUsers) {
        return (
            <div className="flex min-h-screen items-center justify-center bg-slate-50 p-6">
                <Card className="w-full max-w-md">
                    <CardHeader className="items-center text-center">
                        <ShieldAlert className="h-9 w-9 text-red-600" />
                        <CardTitle>Acesso restrito</CardTitle>
                        <CardDescription>Somente administradores da empresa podem gerenciar usuários.</CardDescription>
                    </CardHeader>
                </Card>
            </div>
        );
    }

    return (
        <SidebarProvider>
            <div className="min-h-screen flex w-full bg-background">
                <AppSidebar />
                <main className="min-w-0 flex-1 p-4 md:p-8">
                    <div className="mx-auto max-w-6xl space-y-6">
                        <header className="flex items-center justify-between gap-3">
                            <div className="flex items-center gap-3">
                                <SidebarTrigger />
                                <div>
                                    <h1 className="text-2xl font-bold tracking-tight">Usuários</h1>
                                    <p className="text-sm text-muted-foreground">Gerencie os acessos da sua empresa.</p>
                                </div>
                            </div>
                            <Button onClick={() => { setInviteLink(""); setInviteEmailSent(false); setInviteId(""); setInviteOpen(true); }}><UserPlus className="mr-2 h-4 w-4" />Convidar usuário</Button>
                        </header>

                        <Dialog open={inviteOpen} onOpenChange={setInviteOpen}>
                            <DialogContent>
                                <DialogHeader>
                                    <DialogTitle>Convidar usuário</DialogTitle>
                                    <DialogDescription>O convite vale por sete dias. Com o envio de e-mail configurado, o sistema entrega o link automaticamente; caso contrário, você poderá compartilhá-lo manualmente.</DialogDescription>
                                </DialogHeader>
                                <form onSubmit={(event) => void submitInvitation(event)} className="space-y-4">
                                    <div className="space-y-2">
                                        <label htmlFor="invite-email" className="text-sm font-medium">E-mail</label>
                                        <Input id="invite-email" type="email" autoComplete="email" maxLength={320} required disabled={creatingInvite || !!inviteId} value={inviteEmail} onChange={(event) => { setInviteEmail(event.target.value); setInviteLink(""); }} />
                                    </div>
                                    <div className="space-y-2">
                                        <label htmlFor="invite-role" className="text-sm font-medium">Papel inicial</label>
                                        <select id="invite-role" disabled={creatingInvite || !!inviteId} value={inviteRole} onChange={(event) => { setInviteRole(event.target.value as "Admin" | "Operator"); setInviteLink(""); }} className="h-10 w-full rounded-md border border-input bg-background px-3 text-sm disabled:opacity-50">
                                            <option value="Operator">Operador</option>
                                            <option value="Admin">Administrador</option>
                                        </select>
                                    </div>
                                    <Button type="submit" disabled={creatingInvite} className="w-full">{creatingInvite ? "Gerando convite..." : inviteId ? "Gerar novo convite" : "Gerar convite"}</Button>
                                </form>
                                {inviteId && (
                                    <div className="space-y-2 rounded-md border bg-muted/40 p-3">
                                        {inviteEmailSent ? (
                                            <p className="text-sm font-medium">Convite enviado para {inviteEmail}. O link é de uso único e expira em {new Date(inviteExpiresAt).toLocaleString("pt-BR")}.</p>
                                        ) : (
                                            <>
                                                <p className="text-sm font-medium">Link de uso único</p>
                                                <Input readOnly value={inviteLink} aria-label="Link de convite" onFocus={(event) => event.currentTarget.select()} />
                                            </>
                                        )}
                                        <div className="flex gap-2">
                                            {!inviteEmailSent && <Button type="button" variant="outline" className="flex-1" onClick={() => void copyInvitationLink()}><Copy className="mr-2 h-4 w-4" />Copiar link</Button>}
                                            <Button type="button" variant="destructive" aria-label="Revogar convite" onClick={() => void revokeInvitation()}><X className="h-4 w-4" /></Button>
                                        </div>
                                        {!inviteEmailSent && <p className="text-xs text-muted-foreground">Expira em {new Date(inviteExpiresAt).toLocaleString("pt-BR")}.</p>}
                                    </div>
                                )}
                            </DialogContent>
                        </Dialog>

                        <Card>
                            <CardHeader className="gap-4 border-b sm:flex-row sm:items-center sm:justify-between">
                                <div>
                                    <CardTitle className="flex items-center gap-2"><UsersIcon className="h-5 w-5" />Equipe da empresa</CardTitle>
                                    <CardDescription className="mt-1">Administradores podem gerenciar usuários; operadores têm acesso operacional.</CardDescription>
                                </div>
                                <form onSubmit={submitSearch} className="flex w-full gap-2 sm:max-w-sm">
                                    <Input value={searchInput} onChange={(event) => setSearchInput(event.target.value)} placeholder="Buscar por nome ou e-mail" aria-label="Buscar usuários" />
                                    <Button type="submit" variant="outline" aria-label="Buscar"><Search className="h-4 w-4" /></Button>
                                </form>
                            </CardHeader>
                            <CardContent className="p-0">
                                <div className="overflow-x-auto">
                                    <Table>
                                        <TableHeader>
                                            <TableRow>
                                                <TableHead>Nome</TableHead>
                                                <TableHead>E-mail</TableHead>
                                                <TableHead>Papel</TableHead>
                                                <TableHead>Status</TableHead>
                                                <TableHead>Acesso</TableHead>
                                                <TableHead className="text-right">Cadastro</TableHead>
                                            </TableRow>
                                        </TableHeader>
                                        <TableBody>
                                            {loading ? (
                                                <TableRow><TableCell colSpan={6} className="h-24 text-center text-muted-foreground">Carregando usuários...</TableCell></TableRow>
                                            ) : data?.items.length ? data.items.map((item) => (
                                                <TableRow key={item.id}>
                                                    <TableCell className="font-medium">{item.name}{item.id === user?.id && <span className="ml-2 text-xs text-muted-foreground">(você)</span>}</TableCell>
                                                    <TableCell>{item.email}</TableCell>
                                                    <TableCell>
                                                        {item.id === user?.id ? (
                                                            <Badge variant={item.role === "Admin" ? "default" : "secondary"}>{item.role === "Admin" ? "Administrador" : "Operador"}</Badge>
                                                        ) : (
                                                            <select
                                                                aria-label={`Papel de ${item.name}`}
                                                                value={item.role === "Admin" ? "Admin" : "Operator"}
                                                                disabled={changingUserId === item.id}
                                                                onChange={(event) => void changeRole(item.id, event.target.value as "Admin" | "Operator")}
                                                                className="h-9 rounded-md border border-input bg-background px-3 text-sm disabled:opacity-50"
                                                            >
                                                                <option value="Operator">Operador</option>
                                                                <option value="Admin">Administrador</option>
                                                            </select>
                                                        )}
                                                    </TableCell>
                                                    <TableCell>
                                                        {item.id === user?.id ? (
                                                            <Badge variant={item.isActive ? "default" : "secondary"}>{item.isActive ? "Ativo" : "Inativo"}</Badge>
                                                        ) : (
                                                            <Button
                                                                variant={item.isActive ? "outline" : "secondary"}
                                                                size="sm"
                                                                disabled={changingUserId === item.id}
                                                                onClick={() => void changeActiveStatus(item.id, !item.isActive)}
                                                            >
                                                                {item.isActive ? "Desativar" : "Reativar"}
                                                            </Button>
                                                        )}
                                                    </TableCell>
                                                    <TableCell>
                                                        {item.id === user?.id ? <span className="text-sm text-muted-foreground">—</span> : (
                                                            <Button variant="ghost" size="sm" disabled={!item.isActive || creatingReset} onClick={() => void createRecoveryLink(item.id, item.name)}>
                                                                <KeyRound className="mr-2 h-4 w-4" />Recuperar acesso
                                                            </Button>
                                                        )}
                                                    </TableCell>
                                                    <TableCell className="text-right text-sm text-muted-foreground">{new Date(item.createdAt).toLocaleDateString("pt-BR")}</TableCell>
                                                </TableRow>
                                            )) : (
                                                <TableRow><TableCell colSpan={6} className="h-24 text-center text-muted-foreground">Nenhum usuário encontrado.</TableCell></TableRow>
                                            )}
                                        </TableBody>
                                    </Table>
                                </div>
                                {data && data.totalPages > 1 && (
                                    <div className="flex items-center justify-between border-t px-4 py-3">
                                        <span className="text-sm text-muted-foreground">Página {data.pageNumber} de {data.totalPages} · {data.totalItems} usuários</span>
                                        <div className="flex gap-2">
                                            <Button variant="outline" size="sm" onClick={() => setPage((value) => Math.max(1, value - 1))} disabled={page <= 1 || loading}><ChevronLeft className="mr-1 h-4 w-4" />Anterior</Button>
                                            <Button variant="outline" size="sm" onClick={() => setPage((value) => Math.min(data.totalPages, value + 1))} disabled={page >= data.totalPages || loading}>Próxima<ChevronRight className="ml-1 h-4 w-4" /></Button>
                                        </div>
                                    </div>
                                )}
                            </CardContent>
                        </Card>
                        <Dialog open={resetOpen} onOpenChange={setResetOpen}>
                            <DialogContent>
                                <DialogHeader>
                                    <DialogTitle>Recuperação de acesso</DialogTitle>
                                    <DialogDescription>Link de uso único para {resetTargetName}. Ele expira em uma hora; gere outro para invalidar este link.</DialogDescription>
                                </DialogHeader>
                                <div className="space-y-3">
                                    {resetEmailSent ? (
                                        <p className="text-sm">O link de recuperação foi enviado para o e-mail cadastrado. Ele expira em {new Date(resetExpiresAt).toLocaleString("pt-BR")}.</p>
                                    ) : (
                                        <>
                                            <Input readOnly value={resetLink} aria-label="Link de recuperação" onFocus={(event) => event.currentTarget.select()} />
                                            <Button type="button" variant="outline" className="w-full" onClick={() => void copyResetLink()}><Copy className="mr-2 h-4 w-4" />Copiar link</Button>
                                        </>
                                    )}
                                    <p className="text-xs text-muted-foreground">Expira em {new Date(resetExpiresAt).toLocaleString("pt-BR")}.</p>
                                    <Button type="button" variant="destructive" className="w-full" disabled={creatingReset} onClick={() => void createRecoveryLink(resetTargetId, resetTargetName)}>{resetEmailSent ? "Enviar outro link e invalidar este" : "Gerar outro link e invalidar este"}</Button>
                                    {!resetEmailSent && <p className="text-xs text-muted-foreground">Se o envio de e-mail estiver configurado, ele será enviado ao usuário. Caso contrário, compartilhe o link por um canal seguro; a senha será alterada e as sessões encerradas quando ele for utilizado.</p>}
                                </div>
                            </DialogContent>
                        </Dialog>
                    </div>
                </main>
            </div>
        </SidebarProvider>
    );
}
