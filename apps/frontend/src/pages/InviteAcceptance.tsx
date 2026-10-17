import { useState, type FormEvent } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { PrivacyPolicyModal } from "@/components/PrivacyPolicyModal";
import { useAuth } from "@/contexts/AuthContext";
import { toast } from "sonner";

export default function InviteAcceptance() {
    const [searchParams] = useSearchParams();
    const invitationToken = searchParams.get("token") ?? "";
    const { acceptInvitation } = useAuth();
    const navigate = useNavigate();
    const [name, setName] = useState("");
    const [cpf, setCpf] = useState("");
    const [password, setPassword] = useState("");
    const [confirmPassword, setConfirmPassword] = useState("");
    const [acceptedPrivacyPolicy, setAcceptedPrivacyPolicy] = useState(false);
    const [isPolicyOpen, setIsPolicyOpen] = useState(false);
    const [submitting, setSubmitting] = useState(false);

    const submit = async (event: FormEvent<HTMLFormElement>) => {
        event.preventDefault();
        if (password !== confirmPassword) {
            toast.error("As senhas não coincidem.");
            return;
        }
        if (!invitationToken) {
            toast.error("O link de convite está incompleto.");
            return;
        }

        setSubmitting(true);
        try {
            await acceptInvitation({
                invitationToken,
                name,
                cpf,
                password,
                acceptedPrivacyPolicy,
            });
            toast.success("Convite aceito. Sua conta está pronta.");
            navigate("/dashboard", { replace: true });
        } catch (error) {
            toast.error(error instanceof Error ? error.message : "Não foi possível aceitar o convite.");
        } finally {
            setSubmitting(false);
        }
    };

    return (
        <main className="flex min-h-screen items-center justify-center bg-slate-50 p-4">
            <Card className="w-full max-w-lg shadow-sm">
                <CardHeader>
                    <CardTitle>Aceitar convite</CardTitle>
                    <CardDescription>Crie seu acesso à empresa usando os dados solicitados abaixo.</CardDescription>
                </CardHeader>
                <CardContent>
                    {!invitationToken ? (
                        <p className="text-sm text-destructive">O link não contém um convite válido. Peça ao administrador que gere outro.</p>
                    ) : (
                        <form onSubmit={(event) => void submit(event)} className="space-y-4">
                            <div className="space-y-2">
                                <label htmlFor="invite-name" className="text-sm font-medium">Nome completo</label>
                                <Input id="invite-name" autoComplete="name" maxLength={200} required value={name} onChange={(event) => setName(event.target.value)} />
                            </div>
                            <div className="space-y-2">
                                <label htmlFor="invite-cpf" className="text-sm font-medium">CPF</label>
                                <Input id="invite-cpf" inputMode="numeric" autoComplete="off" minLength={11} maxLength={14} required value={cpf} onChange={(event) => setCpf(event.target.value)} />
                            </div>
                            <div className="space-y-2">
                                <label htmlFor="invite-password" className="text-sm font-medium">Crie uma senha</label>
                                <Input id="invite-password" type="password" autoComplete="new-password" minLength={12} required value={password} onChange={(event) => setPassword(event.target.value)} />
                                <p className="text-xs text-muted-foreground">Use pelo menos 12 caracteres.</p>
                            </div>
                            <div className="space-y-2">
                                <label htmlFor="invite-confirm-password" className="text-sm font-medium">Confirme a senha</label>
                                <Input id="invite-confirm-password" type="password" autoComplete="new-password" minLength={12} required value={confirmPassword} onChange={(event) => setConfirmPassword(event.target.value)} />
                            </div>
                            <label className="flex items-start gap-2 text-sm leading-5">
                                <input className="mt-1" type="checkbox" checked={acceptedPrivacyPolicy} onChange={(event) => setAcceptedPrivacyPolicy(event.target.checked)} required />
                                <span>Li e aceito a <button type="button" onClick={() => setIsPolicyOpen(true)} className="text-primary underline underline-offset-4">Política de Privacidade</button>.</span>
                            </label>
                            <Button type="submit" className="w-full" disabled={submitting || !acceptedPrivacyPolicy}>
                                {submitting ? "Validando convite..." : "Aceitar convite e criar conta"}
                            </Button>
                        </form>
                    )}
                </CardContent>
            </Card>
            <PrivacyPolicyModal isOpen={isPolicyOpen} onClose={() => setIsPolicyOpen(false)} />
        </main>
    );
}
