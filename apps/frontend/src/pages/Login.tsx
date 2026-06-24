import { useState, useEffect } from "react";
import { useNavigate } from "react-router-dom";
import { Package, Loader2 } from "lucide-react";
import { useAuth } from "@/contexts/AuthContext";
import { PrivacyPolicyModal } from "@/components/PrivacyPolicyModal";

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
  const [isSubmitting, setIsSubmitting] = useState(false);
  const { login, register, token } = useAuth();
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

  const handleLogin = async (e: React.FormEvent) => {
    e.preventDefault();
    setError("");
    setIsSubmitting(true);

    try {
      await login(email, password);
      navigate("/dashboard");
    } catch (err: any) {
      setError(err.message || "Ocorreu um erro ao tentar entrar. Tente novamente.");
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
    if (regPassword.length < 8) {
      setError("A senha deve ter no mínimo 8 caracteres.");
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
      await register(regName, regEmail, regCpf, regPassword, regCompanyName, regCompanyCnpj);
      navigate("/dashboard");
    } catch (err: any) {
      setError(err.message || "Ocorreu um erro ao tentar se cadastrar. Tente novamente.");
    } finally {
      setIsSubmitting(false);
    }
  };

  const handleToggleMode = () => {
    setIsRegistering(!isRegistering);
    setError("");
  };

  return (
    <div className="flex min-h-screen w-full bg-white font-sans text-gray-900">
      {/* Left Pane - Background Image */}
      <div className="hidden md:flex md:w-1/2 relative">
        <img
          src={`${import.meta.env.BASE_URL}vending-bg.png`}
          alt="Vending Machine"
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
                ? "Preencha as informações para registrar sua empresa e usuário administrador."
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
                    className="w-full px-3 py-1.5 border border-gray-300 rounded-md bg-gray-50 text-sm placeholder:text-gray-400 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:bg-white transition-colors"
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
                    className="w-full px-3 py-1.5 border border-gray-300 rounded-md bg-gray-50 text-sm placeholder:text-gray-400 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:bg-white transition-colors"
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
                      className="w-full px-3 py-1.5 border border-gray-300 rounded-md bg-gray-50 text-sm placeholder:text-gray-400 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:bg-white transition-colors font-mono"
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
                        placeholder="Mínimo 8 caracteres"
                        className="w-full px-3 py-1.5 border border-gray-300 rounded-md bg-gray-50 text-sm placeholder:text-gray-400 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:bg-white transition-colors"
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
                        placeholder="Mínimo 8 caracteres"
                        className="w-full px-3 py-1.5 border border-gray-300 rounded-md bg-gray-50 text-sm placeholder:text-gray-400 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:bg-white transition-colors"
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
                        className="w-full px-3 py-1.5 border border-gray-300 rounded-md bg-gray-50 text-sm placeholder:text-gray-400 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:bg-white transition-colors"
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
                        className="w-full px-3 py-1.5 border border-gray-300 rounded-md bg-gray-50 text-sm placeholder:text-gray-400 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:bg-white transition-colors font-mono"
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
                  "Finalizar Cadastro"
                )}
              </button>
            </form>
          ) : (
            /* Formulário de Login */
            <form onSubmit={handleLogin} className="space-y-6">
              <div className="space-y-4">
                <div className="space-y-2">
                  <label className="text-sm font-medium text-gray-700" htmlFor="email">
                    Email Address
                  </label>
                  <input
                    id="email"
                    type="email"
                    placeholder="Enter Email Address"
                    className="w-full px-3 py-2 border border-gray-300 rounded-md bg-gray-50 text-sm placeholder:text-gray-400 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:bg-white transition-colors"
                    value={email}
                    onChange={(e) => setEmail(e.target.value)}
                    required
                  />
                </div>

                <div className="space-y-2">
                  <div className="flex items-center justify-between">
                    <label className="text-sm font-medium text-gray-700" htmlFor="password">
                      Password
                    </label>
                  </div>
                  <input
                    id="password"
                    type="password"
                    placeholder="Enter Password"
                    className="w-full px-3 py-2 border border-gray-300 rounded-md bg-gray-50 text-sm placeholder:text-gray-400 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:bg-white transition-colors"
                    value={password}
                    onChange={(e) => setPassword(e.target.value)}
                    required
                  />
                  <div className="flex justify-end pt-1">
                    <a href="#" className="text-sm font-medium text-gray-600 hover:text-blue-600">
                      Forgot Password?
                    </a>
                  </div>
                </div>
              </div>

              {error && (
                <div className="text-sm text-red-600 font-medium">{error}</div>
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

              <div className="relative my-6">
                <div className="absolute inset-0 flex items-center">
                  <div className="w-full border-t border-gray-200"></div>
                </div>
                <div className="relative flex justify-center text-sm">
                  <span className="px-2 bg-white text-gray-500"></span>
                </div>
              </div>

              <button
                type="button"
                className="w-full bg-white border border-gray-300 hover:bg-gray-50 text-gray-700 font-medium py-2 px-4 rounded-md flex items-center justify-center gap-2 transition-colors"
              >
                <svg width="18" height="18" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg">
                  <path d="M22.56 12.25C22.56 11.47 22.49 10.72 22.36 10H12V14.26H17.92C17.66 15.63 16.88 16.79 15.72 17.57V20.34H19.28C21.36 18.42 22.56 15.6 22.56 12.25Z" fill="#4285F4"/>
                  <path d="M12 23C14.97 23 17.46 22.02 19.28 20.34L15.72 17.57C14.74 18.23 13.48 18.63 12 18.63C9.13999 18.63 6.70999 16.7 5.83999 14.11H2.16998V16.96C3.97998 20.55 7.69999 23 12 23Z" fill="#34A853"/>
                  <path d="M5.84 14.11C5.62 13.45 5.49 12.74 5.49 12C5.49 11.26 5.62 10.55 5.84 9.89V7.04H2.17C1.43 8.52 1 10.21 1 12C1 13.79 1.43 15.48 2.17 16.96L5.84 14.11Z" fill="#FBBC05"/>
                  <path d="M12 5.38C13.62 5.38 15.06 5.94 16.21 7.03L19.36 3.88C17.45 2.09 14.97 1 12 1C7.7 1 3.98 3.45 2.17 7.04L5.84 9.89C6.71 7.3 9.14 5.38 12 5.38Z" fill="#EA4335"/>
                </svg>
                Log in with Google
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
                Need an account?{" "}
                <button 
                  onClick={handleToggleMode}
                  className="font-medium text-blue-600 hover:text-blue-500 bg-transparent border-0 p-0 cursor-pointer"
                >
                  Create an account
                </button>
              </>
            )}
          </p>

          <p className="text-xs text-gray-400 mt-6 text-center pt-4">
            © 2026 VendMachine.com.br - All Rights Reserved.
          </p>
        </div>
      </div>
      <PrivacyPolicyModal isOpen={isPolicyOpen} onClose={() => setIsPolicyOpen(false)} />
    </div>
  );
}
