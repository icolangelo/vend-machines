import { useState, useEffect } from "react";
import { useNavigate } from "react-router-dom";
import { Package, Loader2 } from "lucide-react";
import { useAuth } from "@/contexts/AuthContext";
import { PrivacyPolicyModal } from "@/components/PrivacyPolicyModal";
import { getErrorMessage } from "@/lib/utils";

const formatCPF = (value: string) => {
  return value
    .replace(/\D/g, "")
    .replace(/(\d{3})(\d)/, "$1.$2")
    .replace(/(\d{3})(\d)/, "$1.$2")
    .replace(/(\d{3})(\d{1,2})/, "$1-$2")
    .replace(/(-\d{2})\d+?$/, "$1");
};

const formatCNPJ = (value: string) => {
  return value
    .replace(/\D/g, "")
    .replace(/(\d{2})(\d)/, "$1.$2")
    .replace(/(\d{3})(\d)/, "$1.$2")
    .replace(/(\d{3})(\d)/, "$1/$2")
    .replace(/(\d{4})(\d{1,2})/, "$1-$2")
    .replace(/(-\d{2})\d+?$/, "$1");
};

export default function Login() {
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState("");
  const [registrationNotice, setRegistrationNotice] = useState("");
  const [isSubmitting, setIsSubmitting] = useState(false);
  const { login, register, token, sessionMessage, clearSessionMessage } = useAuth();
  const navigate = useNavigate();

  // Estados para Cadastro
  const [isRegistering, setIsRegistering] = useState(false);
  const [regName, setRegName] = useState("");
  const [regEmail, setRegEmail] = useState("");
  const [regCpf, setRegCpf] = useState("");
  const [regPassword, setRegPassword] = useState("");
  const [regConfirmPassword, setRegConfirmPassword] = useState("");
  const [regCompanyName, setRegCompanyName] = useState("");
  const [regCompanyCnpj, setRegCompanyCnpj] = useState("");
  
  // Estado de conformidade LGPD
  const [isPolicyOpen, setIsPolicyOpen] = useState(false);
  const [agreedToPolicy, setAgreedToPolicy] = useState(false);

  useEffect(() => {
    if (token) {
      navigate("/dashboard");
    }
  }, [token, navigate]);

  useEffect(() => {
    if (sessionMessage) {
      setError(sessionMessage);
      clearSessionMessage();
    }
  }, [clearSessionMessage, sessionMessage]);

  const handleLogin = async (e: React.FormEvent) => {
    e.preventDefault();
    setError("");
    setIsSubmitting(true);

    try {
      await login(email, password);
      navigate("/dashboard");
    } catch (err: unknown) {
      setError(getErrorMessage(err, "Ocorreu um erro ao tentar entrar. Tente novamente."));
    } finally {
      setIsSubmitting(false);
    }
  };

  const handleRegister = async (e: React.FormEvent) => {
    e.preventDefault();
    setError("");

    // Validações básicas no front
    if (regCpf.length !== 14) {
      setError("Por favor, preencha o CPF completo.");
      return;
    }
    if (regPassword.length < 12 || regPassword.length > 256) {
      setError("A senha deve ter entre 12 e 256 caracteres.");
      return;
    }
    if (regPassword !== regConfirmPassword) {
      setError("As senhas informadas não coincidem.");
      return;
    }
    if (regCompanyCnpj.length !== 18) {
      setError("Por favor, preencha o CNPJ completo da empresa.");
      return;
    }
    if (!agreedToPolicy) {
      setError("Você precisa ler e concordar com a Política de Privacidade para continuar.");
      return;
    }

    setIsSubmitting(true);

    try {
      const message = await register(regName, regEmail, regCpf, regPassword, regCompanyName, regCompanyCnpj);
      setRegistrationNotice(message);
      setIsRegistering(false);
      setError("");
    } catch (err: unknown) {
      setError(getErrorMessage(err, "Ocorreu um erro ao tentar se cadastrar. Tente novamente."));
    } finally {
      setIsSubmitting(false);
    }
  };

  const handleToggleMode = () => {
    setIsRegistering(!isRegistering);
    setError("");
    setRegistrationNotice("");
  };

  return (
    <div className="flex min-h-screen w-full bg-white font-sans text-gray-900">
      {/* Left Pane - Background Image */}
      <div className="hidden md:flex md:w-1/2 relative">
        <img
          src={`${import.meta.env.BASE_URL}vending-bg.png`}
          alt="Máquina de vendas automática"
          className="w-full h-full object-cover"
        />
        <div className="absolute inset-0 bg-blue-900/20 mix-blend-multiply" />
      </div>

      {/* Right Pane - Login / Registration Form */}
      <div className="w-full md:w-1/2 flex items-center justify-center p-8 sm:p-12 lg:p-16 overflow-y-auto">
        <div className="w-full max-w-md space-y-6">
          
          {/* Header/Logo */}
          <div className="flex items-center gap-2 mb-4 text-blue-600">
            <Package className="w-8 h-8" />
            <span className="text-2xl font-bold tracking-tight text-gray-900">VendMachine.com.br</span>
          </div>

          <div className="space-y-1">
            <h1 className="text-2xl font-bold tracking-tight text-gray-900">
              {isRegistering ? "Criar uma nova conta" : "Entrar na sua conta"}
            </h1>
            <p className="text-sm text-gray-500">
              {isRegistering 
                ? "Solicite o cadastro da empresa e do usuário administrador. O acesso será liberado após análise da plataforma."
                : "Insira suas credenciais de acesso abaixo."}
            </p>
          </div>

          {isRegistering ? (
            /* Formulário de Cadastro */
            <form onSubmit={handleRegister} className="space-y-4">
              <div className="space-y-3">
                <div className="space-y-1">
                  <label className="text-xs font-semibold text-gray-700" htmlFor="regName">
                    Nome Completo
                  </label>
                  <input
                    id="regName"
                    type="text"
                    placeholder="Ex: João da Silva"
                    className="w-full px-3 py-1.5 border border-gray-300 rounded-md bg-gray-50 text-sm placeholder:text-gray-400 focus:outline-hidden focus:ring-2 focus:ring-blue-500 focus:bg-white transition-colors"
                    value={regName}
                    onChange={(e) => setRegName(e.target.value)}
                    required
                  />
                </div>

                <div className="space-y-1">
                  <label className="text-xs font-semibold text-gray-700" htmlFor="regEmail">
                    E-mail Comercial
                  </label>
                  <input
                    id="regEmail"
                    type="email"
                    placeholder="Ex: joao@suaempresa.com"
                    className="w-full px-3 py-1.5 border border-gray-300 rounded-md bg-gray-50 text-sm placeholder:text-gray-400 focus:outline-hidden focus:ring-2 focus:ring-blue-500 focus:bg-white transition-colors"
                    value={regEmail}
                    onChange={(e) => setRegEmail(e.target.value)}
                    required
                  />
                </div>

                <div className="space-y-3">
                  <div className="space-y-1">
                    <label className="text-xs font-semibold text-gray-700" htmlFor="regCpf">
                      CPF
                    </label>
                    <input
                      id="regCpf"
                      type="text"
                      maxLength={14}
                      placeholder="000.000.000-00"
                      className="w-full px-3 py-1.5 border border-gray-300 rounded-md bg-gray-50 text-sm placeholder:text-gray-400 focus:outline-hidden focus:ring-2 focus:ring-blue-500 focus:bg-white transition-colors font-mono"
                      value={regCpf}
                      onChange={(e) => setRegCpf(formatCPF(e.target.value))}
                      required
                    />
                  </div>

                  <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                    <div className="space-y-1">
                      <label className="text-xs font-semibold text-gray-700" htmlFor="regPassword">
                        Senha de Acesso
                      </label>
                      <input
                        id="regPassword"
                        type="password"
                        placeholder="Mínimo 12 caracteres"
                        minLength={12}
                        maxLength={256}
                        className="w-full px-3 py-1.5 border border-gray-300 rounded-md bg-gray-50 text-sm placeholder:text-gray-400 focus:outline-hidden focus:ring-2 focus:ring-blue-500 focus:bg-white transition-colors"
                        value={regPassword}
                        onChange={(e) => setRegPassword(e.target.value)}
                        required
                      />
                    </div>

                    <div className="space-y-1">
                      <label className="text-xs font-semibold text-gray-700" htmlFor="regConfirmPassword">
                        Confirmar Senha
                      </label>
                      <input
                        id="regConfirmPassword"
                        type="password"
                        placeholder="Mínimo 12 caracteres"
                        minLength={12}
                        maxLength={256}
                        className="w-full px-3 py-1.5 border border-gray-300 rounded-md bg-gray-50 text-sm placeholder:text-gray-400 focus:outline-hidden focus:ring-2 focus:ring-blue-500 focus:bg-white transition-colors"
                        value={regConfirmPassword}
                        onChange={(e) => setRegConfirmPassword(e.target.value)}
                        required
                      />
                    </div>
                  </div>
                </div>

                <div className="border-t pt-3 mt-1 border-gray-100">
                  <h3 className="text-xs font-bold text-gray-400 uppercase tracking-wider mb-2">Dados da Empresa</h3>
                  
                  <div className="space-y-3">
                    <div className="space-y-1">
                      <label className="text-xs font-semibold text-gray-700" htmlFor="regCompanyName">
                        Nome da Empresa
                      </label>
                      <input
                        id="regCompanyName"
                        type="text"
                        placeholder="Ex: Vending Brasil Ltda"
                        className="w-full px-3 py-1.5 border border-gray-300 rounded-md bg-gray-50 text-sm placeholder:text-gray-400 focus:outline-hidden focus:ring-2 focus:ring-blue-500 focus:bg-white transition-colors"
                        value={regCompanyName}
                        onChange={(e) => setRegCompanyName(e.target.value)}
                        required
                      />
                    </div>

                    <div className="space-y-1">
                      <label className="text-xs font-semibold text-gray-700" htmlFor="regCompanyCnpj">
                        CNPJ
                      </label>
                      <input
                        id="regCompanyCnpj"
                        type="text"
                        maxLength={18}
                        placeholder="00.000.000/0000-00"
                        className="w-full px-3 py-1.5 border border-gray-300 rounded-md bg-gray-50 text-sm placeholder:text-gray-400 focus:outline-hidden focus:ring-2 focus:ring-blue-500 focus:bg-white transition-colors font-mono"
                        value={regCompanyCnpj}
                        onChange={(e) => setRegCompanyCnpj(formatCNPJ(e.target.value))}
                        required
                      />
                    </div>
                  </div>
                </div>
              </div>

              {/* Consentimento da Política de Privacidade */}
              <div className="flex items-start gap-2 pt-1 pb-1">
                <input
                  id="agreedToPolicy"
                  type="checkbox"
                  className="mt-0.5 h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500 cursor-pointer"
                  checked={agreedToPolicy}
                  onChange={(e) => setAgreedToPolicy(e.target.checked)}
                />
                <label htmlFor="agreedToPolicy" className="text-xs text-gray-600 select-none cursor-pointer leading-tight">
                  Li e concordo com a{" "}
                  <button
                    type="button"
                    onClick={() => setIsPolicyOpen(true)}
                    className="text-blue-600 hover:underline font-semibold bg-transparent border-0 p-0 cursor-pointer align-baseline"
                  >
                    Política de Privacidade
                  </button>
                  .
                </label>
              </div>

              {error && (
                <div className="text-xs text-red-600 font-semibold">{error}</div>
              )}

              <button
                type="submit"
                disabled={isSubmitting}
                className="w-full bg-blue-600 hover:bg-blue-700 disabled:bg-blue-400 text-white font-medium py-2 px-4 rounded-md transition-colors flex items-center justify-center gap-2"
              >
                {isSubmitting ? (
                  <>
                    <Loader2 className="w-4 h-4 animate-spin" />
                    Cadastrando...
                  </>
                ) : (
                  "Enviar cadastro para análise"
                )}
              </button>
            </form>
          ) : (
            /* Formulário de Login */
            <form onSubmit={handleLogin} className="space-y-6">
              <div className="space-y-4">
                <div className="space-y-2">
                  <label className="text-sm font-medium text-gray-700" htmlFor="email">
                    E-mail
                  </label>
                  <input
                    id="email"
                    type="email"
                    placeholder="Digite seu e-mail"
                    className="w-full px-3 py-2 border border-gray-300 rounded-md bg-gray-50 text-sm placeholder:text-gray-400 focus:outline-hidden focus:ring-2 focus:ring-blue-500 focus:bg-white transition-colors"
                    value={email}
                    onChange={(e) => setEmail(e.target.value)}
                    required
                  />
                </div>

                <div className="space-y-2">
                  <div className="flex items-center justify-between">
                    <label className="text-sm font-medium text-gray-700" htmlFor="password">
                      Senha
                    </label>
                  </div>
                  <input
                    id="password"
                    type="password"
                    placeholder="Digite sua senha"
                    className="w-full px-3 py-2 border border-gray-300 rounded-md bg-gray-50 text-sm placeholder:text-gray-400 focus:outline-hidden focus:ring-2 focus:ring-blue-500 focus:bg-white transition-colors"
                    value={password}
                    onChange={(e) => setPassword(e.target.value)}
                    required
                  />
                  <div className="flex justify-end pt-1">
                    <p className="text-right text-xs text-gray-500">Esqueceu a senha? Peça ao administrador da empresa um link de recuperação.</p>
                  </div>
                </div>
              </div>

              {error && (
                <div className="text-sm text-red-600 font-medium">{error}</div>
              )}

              {registrationNotice && (
                <div role="status" className="rounded-md border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-800">
                  {registrationNotice}
                  <button type="button" onClick={() => navigate("/verify-email")} className="mt-2 block font-medium underline underline-offset-2">
                    Solicitar novo link de confirmação
                  </button>
                </div>
              )}

              <button
                type="submit"
                disabled={isSubmitting}
                className="w-full bg-blue-500 hover:bg-blue-600 disabled:bg-blue-400 text-white font-medium py-2.5 px-4 rounded-md transition-colors flex items-center justify-center gap-2"
              >
                {isSubmitting ? (
                  <>
                    <Loader2 className="w-4 h-4 animate-spin" />
                    Entrando...
                  </>
                ) : (
                  "Entrar"
                )}
              </button>
            </form>
          )}

          <p className="text-center text-sm text-gray-600 pt-2">
            {isRegistering ? (
              <>
                Já possui uma conta?{" "}
                <button 
                  onClick={handleToggleMode}
                  className="font-medium text-blue-600 hover:text-blue-500 bg-transparent border-0 p-0 cursor-pointer"
                >
                  Faça login
                </button>
              </>
            ) : (
              <>
                Não tem uma conta?{" "}
                <button 
                  onClick={handleToggleMode}
                  className="font-medium text-blue-600 hover:text-blue-500 bg-transparent border-0 p-0 cursor-pointer"
                >
                  Criar uma conta
                </button>
              </>
            )}
          </p>

          <p className="text-xs text-gray-400 mt-6 text-center pt-4">
            © 2026 VendMachine.com.br - Todos os direitos reservados.
          </p>
        </div>
      </div>
      <PrivacyPolicyModal isOpen={isPolicyOpen} onClose={() => setIsPolicyOpen(false)} />
    </div>
  );
}
