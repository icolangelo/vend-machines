import { useState, type FormEvent } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { resendEmailVerification, verifyEmail } from "@/lib/api";

export default function VerifyEmail() {
    const [searchParams] = useSearchParams();
    const token = searchParams.get("token") ?? "";
    const [email, setEmail] = useState("");
    const [submitting, setSubmitting] = useState(false);
    const [verificationMessage, setVerificationMessage] = useState("");
    const [resendMessage, setResendMessage] = useState("");
    const [error, setError] = useState("");

    const confirmEmail = async (event: FormEvent<HTMLFormElement>) => {
        event.preventDefault();
        setSubmitting(true);
        setError("");
        try {
            const result = await verifyEmail(token);
            setVerificationMessage(result.message);
        } catch (verificationError) {
            setError(verificationError instanceof Error ? verificationError.message : "Não foi possível confirmar o e-mail.");
        } finally {
            setSubmitting(false);
        }
    };

    const requestAnotherLink = async (event: FormEvent<HTMLFormElement>) => {
        event.preventDefault();
        setSubmitting(true);
        setError("");
        setResendMessage("");
        try {
            const result = await resendEmailVerification(email);
            setResendMessage(result.message);
        } catch (resendError) {
            setError(resendError instanceof Error ? resendError.message : "Não foi possível solicitar um novo link.");
        } finally {
            setSubmitting(false);
        }
    };

    return (
        <main className="flex min-h-screen items-center justify-center bg-slate-50 p-4">
            <Card className="w-full max-w-md shadow-sm">
                <CardHeader>
                    <CardTitle>Confirme seu e-mail</CardTitle>
                    <CardDescription>Confirme o endereço usado no cadastro para concluir essa etapa. A liberação da empresa ainda depende da análise da plataforma.</CardDescription>
                </CardHeader>
                <CardContent className="space-y-6">
                    {verificationMessage ? (
                        <div role="status" className="rounded-md border border-emerald-200 bg-emerald-50 p-3 text-sm text-emerald-900">
                            {verificationMessage}
                        </div>
                    ) : token ? (
                        <form onSubmit={(event) => void confirmEmail(event)} className="space-y-3">
                            <p className="text-sm text-muted-foreground">O link só será usado quando você confirmar abaixo.</p>
                            <Button type="submit" className="w-full" disabled={submitting}>
                                {submitting ? "Confirmando e-mail..." : "Confirmar meu e-mail"}
                            </Button>
                        </form>
                    ) : (
                        <p className="text-sm text-muted-foreground">Abra o link enviado para seu e-mail ou solicite outro abaixo.</p>
                    )}

                    {error && <p role="alert" className="text-sm text-destructive">{error}</p>}

                    <form onSubmit={(event) => void requestAnotherLink(event)} className="space-y-3 border-t pt-5">
                        <div className="space-y-2">
                            <label htmlFor="verification-email" className="text-sm font-medium">E-mail do cadastro</label>
                            <Input
                                id="verification-email"
                                type="email"
                                autoComplete="email"
                                maxLength={320}
                                required
                                value={email}
                                onChange={(event) => setEmail(event.target.value)}
                            />
                        </div>
                        <Button type="submit" variant="outline" className="w-full" disabled={submitting}>
                            {submitting ? "Enviando solicitação..." : "Solicitar novo link"}
                        </Button>
                        {resendMessage && <p role="status" className="text-sm text-muted-foreground">{resendMessage}</p>}
                    </form>

                    <p className="text-center text-sm text-muted-foreground">
                        <Link to="/" className="font-medium text-primary hover:underline">Voltar para o login</Link>
                    </p>
                </CardContent>
            </Card>
        </main>
    );
}
