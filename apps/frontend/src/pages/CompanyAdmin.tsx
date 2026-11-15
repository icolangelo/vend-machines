import { useEffect, useState } from "react";
import { AppSidebar } from "@/components/AppSidebar";
import { SidebarProvider, SidebarTrigger } from "@/components/ui/sidebar";
import { useAuth } from "@/contexts/useAuth";
import { useImpersonation } from "@/contexts/useImpersonation";
import { getCompanyAdminLogs, getCompanyAdminUsers, updateCompanyUserRole, type PaginatedUsers, type PaginatedAdminLogs } from "@/lib/api";

export default function CompanyAdmin() {
  const { user } = useAuth();
  const { impersonatedCompany } = useImpersonation();
  const companyKey = user?.role === "PlatformAdmin" ? impersonatedCompany?.id ?? "" : user?.companyId ?? "";
  const [tab, setTab] = useState<"users" | "logs">("users");
  const [users, setUsers] = useState<PaginatedUsers | null>(null);
  const [logs, setLogs] = useState<PaginatedAdminLogs | null>(null);
  const [page, setPage] = useState(1);
  const [search, setSearch] = useState("");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  useEffect(() => {
    setUsers(null); setLogs(null); setPage(1); setError("");
    if (!companyKey) return;
    let active = true; setBusy(true);
    const load = tab === "users" ? getCompanyAdminUsers(page, 10, search) : getCompanyAdminLogs(page, 20, search);
    load.then(data => { if (active) tab === "users" ? setUsers(data as PaginatedUsers) : setLogs(data as PaginatedAdminLogs); })
      .catch(e => { if (active) setError(e instanceof Error ? e.message : "Falha ao carregar dados."); })
      .finally(() => { if (active) setBusy(false); });
    return () => { active = false; };
  }, [companyKey, tab, page, search]);
  const changeRole = async (id: string, name: string, role: "Admin" | "Operator") => {
    if (!window.confirm(`Alterar o perfil de ${name}? As sessões ativas serão encerradas.`)) return;
    try { await updateCompanyUserRole(id, role); setUsers(null); setPage(p => p); setSearch(s => `${s}`); const data = await getCompanyAdminUsers(page, 10, search); setUsers(data); }
    catch (e) { setError(e instanceof Error ? e.message : "Não foi possível alterar o perfil."); }
  };
  return <SidebarProvider><div className="flex min-h-screen w-full"><AppSidebar /><main className="flex-1 p-6 space-y-5 overflow-auto"><SidebarTrigger /><header><h1 className="text-2xl font-bold">Admin</h1><p className="text-muted-foreground">Gerencie usuários e consulte transações da empresa selecionada.</p></header>
    {!companyKey ? <p role="status" className="rounded border p-4">Selecione uma empresa para acessar a administração.</p> : <>
    <nav className="flex gap-2"><button className="rounded border px-4 py-2" onClick={() => { setTab("users"); setPage(1); }}>Usuários</button><button className="rounded border px-4 py-2" onClick={() => { setTab("logs"); setPage(1); }}>Logs de Transações</button></nav>
    <form className="flex gap-2" onSubmit={e => { e.preventDefault(); setPage(1); setSearch(String(new FormData(e.currentTarget).get("search") ?? "")); }}><input name="search" defaultValue={search} placeholder="Buscar por nome, e-mail ou mensagem" className="rounded border px-3 py-2"/><button className="rounded border px-4">Buscar</button></form>
    {error && <p role="alert" className="text-red-700">{error}</p>}{busy && <p>Carregando...</p>}
    {tab === "users" && users && <div className="overflow-auto"><table className="w-full text-left"><thead><tr><th className="p-2">Nome</th><th className="p-2">E-mail</th><th className="p-2">CPF</th><th className="p-2">Perfil</th></tr></thead><tbody>{users.items.map(u => <tr key={u.id} className="border-t"><td className="p-2">{u.name}</td><td className="p-2">{u.email}</td><td className="p-2">{u.cpf || "-"}</td><td className="p-2"><select aria-label={`Perfil de ${u.name}`} disabled={u.id === user?.id || u.role === "PlatformAdmin"} value={u.role === "Admin" ? "Admin" : "Operator"} onChange={e => void changeRole(u.id, u.name, e.target.value as "Admin" | "Operator")}><option value="Operator">Operador</option><option value="Admin">Administrador</option></select></td></tr>)}</tbody></table></div>}
    {tab === "logs" && logs && <div className="overflow-auto"><table className="w-full text-left"><thead><tr><th className="p-2">Data</th><th className="p-2">Tipo</th><th className="p-2">Máquina</th><th className="p-2">Valor</th><th className="p-2">Mensagem</th></tr></thead><tbody>{logs.items.map(l => <tr key={l.id} className="border-t"><td className="p-2">{new Date(l.timestamp).toLocaleString("pt-BR")}</td><td className="p-2">{l.logType}</td><td className="p-2">{l.machineName}</td><td className="p-2">{l.transactionAmount.toLocaleString("pt-BR", { style: "currency", currency: "BRL" })}</td><td className="p-2">{l.message}</td></tr>)}</tbody></table></div>}
    <div className="flex items-center gap-3"><button className="rounded border px-3 py-1" disabled={page <= 1} onClick={() => setPage(p => p - 1)}>Anterior</button><span>Página {page} de {tab === "users" ? users?.totalPages ?? 1 : logs?.totalPages ?? 1}</span><button className="rounded border px-3 py-1" disabled={page >= (tab === "users" ? users?.totalPages ?? 1 : logs?.totalPages ?? 1)} onClick={() => setPage(p => p + 1)}>Próxima</button></div>
    </>}</main></div></SidebarProvider>;
}

