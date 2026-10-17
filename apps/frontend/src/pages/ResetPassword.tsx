import { useState, type FormEvent } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { useAuth } from "@/contexts/AuthContext";
import { resetPassword } from "@/lib/api";
import { toast } from "sonner";

export default function ResetPassword() {
    const [searchParams] = useSearchParams();
    const resetToken = searchParams.get("token") ?? "";
    const navigate = useNavigate();
    const { logout } = useAuth();
    const [password, setPassword] = useState("");
    const [confirmPassword, setConfirmPassword] = useState("");
    const [submitting, setSubmitting] = useState(false);

    const submit = async (event: FormEvent<HTMLFormElement>) => {
        event.preventDefault();
        if (!resetToken) {
            toast.error("O link de recuperação está incompleto.");
            return;
        }
        if (password !== confirmPassword) {
            toast.error("As senhas não coincidem.");
            return;
        }

        setSubmitting(true);
        try {
            await resetPassword(resetToken, password);
            await logout().catch(() => undefined);
            toast.success("Senha atualizada. Entre com a nova senha.");
            navigate("/", { replace: true });
        } catch (error) {
            toast.error(error instanceof Error ? error.message : "Não foi possível redefinir a senha.");
        } finally {
            setSubmitting(false);
        }
    };

    return (
        <main className="flex min-h-screen items-center justify-center bg-slate-50 p-4">
            <Card className="w-full max-w-md shadow-xs">
                <CardHeader>
                    <CardTitle>Recuperar acesso</CardTitle>
                    <CardDescription>Escolha uma senha nova. O link é válido por uma hora e pode ser usado uma vez.</CardDescription>
                </CardHeader>
                <CardContent>
                    {!resetToken ? (
                        <p className="text-sm text-destructive">O link não contém um token válido. Peça ao administrador que gere outro.</p>
                    ) : (
                        <form onSubmit={(event) => void submit(event)} className="space-y-4">
                            <div className="space-y-2">
                                <label htmlFor="new-password" className="text-sm font-medium">Nova senha</label>
                                <Input id="new-password" type="password" autoComplete="new-password" minLength={12} maxLength={256} required value={password} onChange={(event) => setPassword(event.target.value)} />
                                <p className="text-xs text-muted-foreground">Use entre 12 e 256 caracteres.</p>
                            </div>
                            <div className="space-y-2">
                                <label htmlFor="confirm-new-password" className="text-sm font-medium">Confirme a nova senha</label>
                                <Input id="confirm-new-password" type="password" autoComplete="new-password" minLength={12} maxLength={256} required value={confirmPassword} onChange={(event) => setConfirmPassword(event.target.value)} />
                            </div>
                            <Button type="submit" className="w-full" disabled={submitting}>
                                {submitting ? "Atualizando senha..." : "Atualizar senha"}
                            </Button>
                        </form>
                    )}
                </CardContent>
            </Card>
        </main>
    );
}
