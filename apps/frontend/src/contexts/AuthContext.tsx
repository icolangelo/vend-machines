import React, { useCallback, useEffect, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { AuthContext, type AuthContextType } from "@/contexts/authContextDefinition";
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
  type RegistrationResult,
} from "@/lib/authClient";

export type User = AuthUser;

const testSessionStorageKey = "vm-test-auth-session";
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

    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
      restoreMockAuthSession();
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
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
      if (password !== "teste123" && password !== "admin123") {
        throw new Error("Senha incorreta. Por favor, tente novamente.");
      }

      const normalizedEmail = email.trim().toLowerCase();
      const isOperator = normalizedEmail.includes("operador") || normalizedEmail.includes("operator");
      const isSaborCompany = normalizedEmail.endsWith("@sabor.example");
      const isAcmeCompany = normalizedEmail.endsWith("@acme.example");
      const isTenantAdmin = isSaborCompany || isAcmeCompany;
      setMockAuthSession(createMockSession({
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
  ): Promise<RegistrationResult> => {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
      return { kind: "pending", status: "Pending", message: "Cadastro recebido. A empresa aguarda análise da equipe da plataforma." };
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
    return result;
  };

  const acceptInvitation = async (payload: {
    invitationToken: string;
    name: string;
    cpf: string;
    password: string;
    acceptedPrivacyPolicy: boolean;
  }) => {
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
      setMockAuthSession(createMockSession({
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
    if ((import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true")) {
      clearMockAuthSession();
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

function setMockAuthSession(session: AuthSession): void {
  try {
    sessionStorage.setItem(testSessionStorageKey, JSON.stringify(session));
  } catch {
    // O mock continua funcionando na página atual quando o storage está indisponível.
  }
  setAuthSession(session);
}

function restoreMockAuthSession(): void {
  try {
    const serializedSession = sessionStorage.getItem(testSessionStorageKey);
    if (!serializedSession) return;

    const session = JSON.parse(serializedSession) as AuthSession;
    if (Date.parse(session.expiresAt) <= Date.now()) {
      sessionStorage.removeItem(testSessionStorageKey);
      return;
    }

    setAuthSession(session);
  } catch {
    try {
      sessionStorage.removeItem(testSessionStorageKey);
    } catch {
      // A sessão simulada permanece somente na memória desta navegação.
    }
  }
}

function clearMockAuthSession(): void {
  try {
    sessionStorage.removeItem(testSessionStorageKey);
  } catch {
    // Logout do mock não depende da disponibilidade do storage.
  }
}
