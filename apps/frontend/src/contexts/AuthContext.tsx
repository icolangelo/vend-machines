import React, { createContext, useContext, useState, useEffect } from "react";
import { API_BASE_URL } from "@/lib/api";

export interface User {
  id: string;
  name: string;
  email: string;
  role: string;
  companyId?: string | null;
  companyName?: string | null;
}

interface AuthContextType {
  user: User | null;
  token: string | null;
  loading: boolean;
  login: (email: string, password: string) => Promise<void>;
  logout: () => void;
  register: (
    name: string,
    email: string,
    cpf: string,
    password: string,
    companyName: string,
    companyCnpj: string
  ) => Promise<void>;
}

const AuthContext = createContext<AuthContextType | undefined>(undefined);

const isTestEnv = () => import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true";

export const AuthProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const [user, setUser] = useState<User | null>(null);
  const [token, setToken] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    const initializeAuth = async () => {
      const storedToken = sessionStorage.getItem("token") || localStorage.getItem("token");
      const storedUser = sessionStorage.getItem("user") || localStorage.getItem("user");

      if (storedToken) {
        setToken(storedToken);
        if (storedUser) {
          setUser(JSON.parse(storedUser));
        }

        if (isTestEnv()) {
          setLoading(false);
          return;
        }

        try {
          // Validar o token chamando a rota /me no backend
          const response = await fetch(`${API_BASE_URL}/auth/me`, {
            headers: {
              Authorization: `Bearer ${storedToken}`,
            },
          });

          if (response.ok) {
            const userData = await response.json();
            setUser(userData);
            sessionStorage.setItem("user", JSON.stringify(userData));
          } else {
            // Token expirado ou inválido
            logout();
          }
        } catch (error) {
          console.error("Erro ao verificar autenticação inicial:", error);
          // Se for falha de rede temporária, mantemos os dados salvos locais
        }
      }
      setLoading(false);
    };

    initializeAuth();
  }, []);

  const login = async (email: string, password: string) => {
    if (isTestEnv()) {
      if (password === "teste123" || password === "admin123") {
        const isOperator = email.toLowerCase().includes("joao");
        const mockUser = {
          id: isOperator ? "usr-joao" : "usr-admin",
          name: isOperator ? "João da Silva" : "Admin Ivan",
          email: email,
          role: isOperator ? "Operator" : "Admin",
          companyId: "11111111-1111-1111-1111-111111111111",
          companyName: "ACME Machines LTDA"
        };
        const mockToken = "mock-jwt-token-string";
        setUser(mockUser);
        setToken(mockToken);
        sessionStorage.setItem("token", mockToken);
        sessionStorage.setItem("user", JSON.stringify(mockUser));
        sessionStorage.setItem("isAuthenticated", "true");
        return;
      } else {
        throw new Error("Senha incorreta. Por favor, tente novamente.");
      }
    }

    const response = await fetch(`${API_BASE_URL}/auth/login`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
      },
      body: JSON.stringify({ email, password }),
    });

    if (!response.ok) {
      const data = await response.json();
      throw new Error(data.message || "E-mail ou senha incorretos.");
    }

    const data = await response.json();
    setUser(data.user);
    setToken(data.token);
    sessionStorage.setItem("token", data.token);
    sessionStorage.setItem("user", JSON.stringify(data.user));
    sessionStorage.setItem("isAuthenticated", "true");
  };

  const register = async (
    name: string,
    email: string,
    cpf: string,
    password: string,
    companyName: string,
    companyCnpj: string
  ) => {
    if (isTestEnv()) {
      const mockUser = {
        id: "usr-admin-registered",
        name: name,
        email: email,
        role: "Admin",
        companyId: "11111111-1111-1111-1111-111111111111",
        companyName: companyName
      };
      const mockToken = "mock-jwt-token-string";
      setUser(mockUser);
      setToken(mockToken);
      sessionStorage.setItem("token", mockToken);
      sessionStorage.setItem("user", JSON.stringify(mockUser));
      sessionStorage.setItem("isAuthenticated", "true");
      return;
    }

    const response = await fetch(`${API_BASE_URL}/auth/register`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
      },
      body: JSON.stringify({ 
        name, 
        email, 
        cpf, 
        password, 
        companyName, 
        companyCnpj,
        acceptedPrivacyPolicy: true 
      }),
    });

    if (!response.ok) {
      const data = await response.json();
      throw new Error(data.message || "Erro ao realizar o cadastro. Verifique os dados.");
    }

    const data = await response.json();
    setUser(data.user);
    setToken(data.token);
    sessionStorage.setItem("token", data.token);
    sessionStorage.setItem("user", JSON.stringify(data.user));
    sessionStorage.setItem("isAuthenticated", "true");
  };

  const logout = () => {
    setUser(null);
    setToken(null);
    sessionStorage.removeItem("token");
    sessionStorage.removeItem("user");
    sessionStorage.removeItem("isAuthenticated");
    localStorage.removeItem("token");
    localStorage.removeItem("user");
  };

  return (
    <AuthContext.Provider value={{ user, token, loading, login, logout, register }}>
      {children}
    </AuthContext.Provider>
  );
};

export const useAuth = () => {
  const context = useContext(AuthContext);
  if (context === undefined) {
    throw new Error("useAuth deve ser usado dentro de um AuthProvider");
  }
  return context;
};
