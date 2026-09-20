import React, { createContext, useCallback, useContext, useEffect, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import {
  clearAuthSession,
  clearLegacyAuthStorage,
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
  ) => Promise<void>;
}

const AuthContext = createContext<AuthContextType | undefined>(undefined);
const isTestEnv = () => import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true";

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

      const isOperator = email.toLowerCase().includes("operador") || email.toLowerCase().includes("operator");
      setAuthSession(createMockSession({
        id: isOperator ? "usr-joao" : "usr-admin",
        name: isOperator ? "João da Silva" : "Admin Ivan",
        email,
        role: isOperator ? "Operator" : "Admin",
        companyId: "11111111-1111-1111-1111-111111111111",
        companyName: "ACME Machines LTDA",
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
      setAuthSession(createMockSession({
        id: "usr-admin-registered",
        name,
        email,
        role: "Admin",
        companyId: "11111111-1111-1111-1111-111111111111",
        companyName,
      }));
      return;
    }

    await registerSession({
      name,
      email,
      cpf,
      password,
      companyName,
      companyCnpj,
      acceptedPrivacyPolicy: true,
    });
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
