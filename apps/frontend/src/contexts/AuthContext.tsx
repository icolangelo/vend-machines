import React, { createContext, useCallback, useContext, useEffect, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import {
  clearAuthSession,
  clearLegacyAuthStorage,
  acceptInvitationSession,
  loginSession,
  logoutSession,
  registerSession,
  restoreSession,
  setAuthSession,
  subscribeToAuthState,
  type AuthSession,
  type AuthUser,
} from "@/lib/authClient";

export type User = AuthUser;

interface AuthContextType {
  user: User | null;
  token: string | null;
  loading: boolean;
  sessionMessage: string | null;
  clearSessionMessage: () => void;
  login: (email: string, password: string) => Promise<void>;
  logout: () => Promise<void>;
  register: (
    name: string,
    email: string,
    cpf: string,
    password: string,
    companyName: string,
    companyCnpj: string,
  ) => Promise<string>;
  acceptInvitation: (payload: {
    invitationToken: string;
    name: string;
    cpf: string;
    password: string;
    acceptedPrivacyPolicy: boolean;
  }) => Promise<void>;
}

const AuthContext = createContext<AuthContextType | undefined>(undefined);
const isTestEnv = () => import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true";
const TEST_COMPANY_IDS = {
  acme: "11111111-1111-1111-1111-111111111111",
  sabor: "55555555-5555-5555-5555-555555555555",
} as const;

export const AuthProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const queryClient = useQueryClient();
  const [user, setUser] = useState<User | null>(null);
  const [token, setToken] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [sessionMessage, setSessionMessage] = useState<string | null>(null);

  useEffect(() => {
    const applyState = (session: AuthSession | null, reason: "updated" | "expired" | "logout") => {
      setUser(session?.user ?? null);
      setToken(session?.token ?? null);
      if (!session && (reason === "expired" || reason === "logout")) {
        queryClient.clear();
      }
      if (reason === "expired") {
        setSessionMessage("Sua sessão expirou. Entre novamente.");
      } else if (reason === "updated") {
        setSessionMessage(null);
      }
    };

    const unsubscribe = subscribeToAuthState(applyState);
    clearLegacyAuthStorage();

    if (isTestEnv()) {
      setLoading(false);
      return unsubscribe;
    }

    void restoreSession()
      .catch((error) => {
        console.error("Erro ao restaurar a sessão:", error);
      })
      .finally(() => setLoading(false));

    return unsubscribe;
  }, [queryClient]);

  const login = async (email: string, password: string) => {
    if (isTestEnv()) {
      if (password !== "teste123" && password !== "admin123") {
        throw new Error("Senha incorreta. Por favor, tente novamente.");
      }

      const normalizedEmail = email.trim().toLowerCase();
      const isOperator = normalizedEmail.includes("operador") || normalizedEmail.includes("operator");
      const isSaborCompany = normalizedEmail.endsWith("@sabor.example");
      const isAcmeCompany = normalizedEmail.endsWith("@acme.example");
      const isTenantAdmin = isSaborCompany || isAcmeCompany;
      setAuthSession(createMockSession({
        id: isSaborCompany
          ? (isOperator ? "usr-sabor-operator" : "usr-sabor-admin")
          : isAcmeCompany
            ? (isOperator ? "usr-acme-operator" : "usr-acme-admin")
            : (isOperator ? "usr-joao" : "usr-admin"),
        name: isSaborCompany
          ? (isOperator ? "Ana Operadora" : "Admin Sabor")
          : isAcmeCompany
            ? (isOperator ? "Carlos Operador" : "Admin ACME")
            : (isOperator ? "João da Silva" : "Admin Ivan"),
        email,
        role: isOperator ? "Operator" : isTenantAdmin ? "Admin" : "PlatformAdmin",
        companyId: isSaborCompany ? TEST_COMPANY_IDS.sabor : TEST_COMPANY_IDS.acme,
        companyName: isSaborCompany ? "Sabor & Cia Vending" : "ACME Machines LTDA",
      }));
      return;
    }

    await loginSession(email, password);
  };

  const register = async (
    name: string,
    email: string,
    cpf: string,
    password: string,
    companyName: string,
    companyCnpj: string,
  ) => {
    if (isTestEnv()) {
      return "Cadastro recebido. A empresa aguarda análise da equipe da plataforma.";
    }

    const result = await registerSession({
      name,
      email,
      cpf,
      password,
      companyName,
      companyCnpj,
      acceptedPrivacyPolicy: true,
    });
    return result.message;
  };

  const acceptInvitation = async (payload: {
    invitationToken: string;
    name: string;
    cpf: string;
    password: string;
    acceptedPrivacyPolicy: boolean;
  }) => {
    if (isTestEnv()) {
      setAuthSession(createMockSession({
        id: "usr-invited",
        name: payload.name,
        email: "novo.usuario@vendmachine.com.br",
        role: "Operator",
        companyId: "11111111-1111-1111-1111-111111111111",
        companyName: "ACME Machines LTDA",
      }));
      return;
    }

    await acceptInvitationSession(payload);
  };

  const logout = async () => {
    if (isTestEnv()) {
      clearAuthSession("logout");
      return;
    }

    await logoutSession();
  };

  const clearSessionMessage = useCallback(() => setSessionMessage(null), []);

  return (
    <AuthContext.Provider
      value={{
        user,
        token,
        loading,
        sessionMessage,
        clearSessionMessage,
        login,
        logout,
        register,
        acceptInvitation,
      }}
    >
      {children}
    </AuthContext.Provider>
  );
};

function createMockSession(user: User): AuthSession {
  return {
    token: "mock-jwt-token-string",
    expiresAt: new Date(Date.now() + 60 * 60 * 1000).toISOString(),
    user,
  };
}

export const useAuth = () => {
  const context = useContext(AuthContext);
  if (context === undefined) {
    throw new Error("useAuth deve ser usado dentro de AuthProvider");
  }
  return context;
};
