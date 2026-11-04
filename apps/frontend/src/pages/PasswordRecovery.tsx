import { useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { Loader2, Package } from "lucide-react";
import { API_BASE_URL } from "@/lib/authClient";

export default function PasswordRecovery() {
  const [searchParams] = useSearchParams();
  const resetToken = searchParams.get("token") ?? "";
  const isReset = Boolean(resetToken);
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [confirmation, setConfirmation] = useState("");
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");
  const [complete, setComplete] = useState(false);
  const [isSubmitting, setIsSubmitting] = useState(false);

  const handleSubmit = async (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    setMessage("");
    setError("");
    if (isReset && password !== confirmation) {
      setError("As senhas informadas não coincidem.");
      return;
    }

    setIsSubmitting(true);
    try {
      const response = await fetch(`${API_BASE_URL}/auth/${isReset ? "reset-password" : "request-password-reset"}`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(isReset ? { resetToken, password } : { email })
      });
      if (!response.ok) {
        const result = await response.json().catch(() => ({}));
        throw new Error(result.message || "Não foi possível concluir a solicitação. Tente novamente.");
      }
      if (isReset) {
        setComplete(true);
        setMessage("Senha alterada. Você já pode entrar com a nova senha.");
      } else {
        setMessage("Se houver uma conta ativa e verificada para este e-mail, enviaremos instruções de recuperação.");
      }
    } catch (requestError: unknown) {
      setError(requestError instanceof Error ? requestError.message : "Não foi possível concluir a solicitação. Tente novamente.");
    } finally {
      setIsSubmitting(false);
    }
  };

  return (
    <main className="flex min-h-screen items-center justify-center bg-slate-50 p-6">
      <section className="w-full max-w-md space-y-6 rounded-xl border border-gray-200 bg-white p-8 shadow-sm">
        <div className="flex items-center gap-2 text-blue-600">
          <Package className="h-8 w-8" />
          <span className="text-2xl font-bold tracking-tight text-gray-900">VendMachine.com.br</span>
        </div>
        <div className="space-y-2">
          <h1 className="text-2xl font-bold text-gray-900">
            {complete ? "Senha redefinida" : isReset ? "Criar nova senha" : "Recuperar acesso"}
          </h1>
          <p className="text-sm text-gray-600">
            {isReset ? "Escolha uma senha com pelo menos 12 caracteres." : "Informe seu e-mail para receber um link de recuperação, se houver uma conta elegível."}
          </p>
        </div>

        {!complete && (
          <form onSubmit={handleSubmit} className="space-y-4">
            {isReset ? (
              <>
                <div className="space-y-1">
                  <label htmlFor="new-password" className="text-sm font-medium text-gray-700">Nova senha</label>
                  <input id="new-password" type="password" autoComplete="new-password" minLength={12} maxLength={256} required value={password} onChange={(event) => setPassword(event.target.value)} className="w-full rounded-md border border-gray-300 bg-gray-50 px-3 py-2 text-sm focus:bg-white focus:outline-none focus:ring-2 focus:ring-blue-500" />
                </div>
                <div className="space-y-1">
                  <label htmlFor="confirm-password" className="text-sm font-medium text-gray-700">Confirme a nova senha</label>
                  <input id="confirm-password" type="password" autoComplete="new-password" minLength={12} maxLength={256} required value={confirmation} onChange={(event) => setConfirmation(event.target.value)} className="w-full rounded-md border border-gray-300 bg-gray-50 px-3 py-2 text-sm focus:bg-white focus:outline-none focus:ring-2 focus:ring-blue-500" />
                </div>
              </>
            ) : (
              <div className="space-y-1">
                <label htmlFor="recovery-email" className="text-sm font-medium text-gray-700">E-mail</label>
                <input id="recovery-email" type="email" autoComplete="email" maxLength={254} required value={email} onChange={(event) => setEmail(event.target.value)} placeholder="voce@suaempresa.com.br" className="w-full rounded-md border border-gray-300 bg-gray-50 px-3 py-2 text-sm focus:bg-white focus:outline-none focus:ring-2 focus:ring-blue-500" />
              </div>
            )}
            {error && <p role="alert" className="text-sm font-medium text-red-600">{error}</p>}
            <button type="submit" disabled={isSubmitting} className="flex w-full items-center justify-center gap-2 rounded-md bg-blue-600 px-4 py-2.5 font-medium text-white transition-colors hover:bg-blue-700 disabled:bg-blue-400">
              {isSubmitting && <Loader2 className="h-4 w-4 animate-spin" />}
              {isSubmitting ? "Aguarde..." : isReset ? "Salvar nova senha" : "Enviar instruções"}
            </button>
          </form>
        )}

        {message && <p role="status" className="text-sm text-green-700">{message}</p>}
        <Link to="/" className="block text-center text-sm font-medium text-blue-600 hover:text-blue-700">Voltar para entrar</Link>
      </section>
    </main>
  );
}
