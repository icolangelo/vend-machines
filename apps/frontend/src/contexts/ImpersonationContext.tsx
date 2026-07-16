import React, { createContext, useContext, useState, useEffect, useCallback } from "react";
import { useAuth } from "@/contexts/AuthContext";

export const SUPER_ADMIN_EMAIL = "dev.ivan@gmail.com";
const STORAGE_KEY = "impersonatedCompany";

export interface ImpersonatedCompany {
  id: string;
  name: string;
}

interface ImpersonationContextType {
  isSuperAdmin: boolean;
  impersonatedCompany: ImpersonatedCompany | null;
  setImpersonatedCompany: (company: ImpersonatedCompany | null) => void;
  clearImpersonation: () => void;
  effectiveCompanyName: string | null;
}

const ImpersonationContext = createContext<ImpersonationContextType | undefined>(undefined);

export const ImpersonationProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const { user } = useAuth();
  const isSuperAdmin = user?.email === SUPER_ADMIN_EMAIL;

  const [impersonatedCompany, setImpersonatedCompanyState] = useState<ImpersonatedCompany | null>(() => {
    try {
      const stored = sessionStorage.getItem(STORAGE_KEY);
      return stored ? JSON.parse(stored) : null;
    } catch {
      return null;
    }
  });

  // Clear impersonation when user logs out
  useEffect(() => {
    if (!user) {
      sessionStorage.removeItem(STORAGE_KEY);
      setImpersonatedCompanyState(null);
    }
  }, [user]);

  // Clear if not SuperAdmin (safety net)
  useEffect(() => {
    if (!isSuperAdmin && impersonatedCompany) {
      sessionStorage.removeItem(STORAGE_KEY);
      setImpersonatedCompanyState(null);
    }
  }, [isSuperAdmin, impersonatedCompany]);

  const setImpersonatedCompany = useCallback((company: ImpersonatedCompany | null) => {
    if (company) {
      sessionStorage.setItem(STORAGE_KEY, JSON.stringify(company));
    } else {
      sessionStorage.removeItem(STORAGE_KEY);
    }
    setImpersonatedCompanyState(company);
  }, []);

  const clearImpersonation = useCallback(() => {
    setImpersonatedCompany(null);
  }, [setImpersonatedCompany]);

  const effectiveCompanyName = impersonatedCompany?.name ?? user?.companyName ?? null;

  return (
    <ImpersonationContext.Provider
      value={{
        isSuperAdmin,
        impersonatedCompany,
        setImpersonatedCompany,
        clearImpersonation,
        effectiveCompanyName,
      }}
    >
      {children}
    </ImpersonationContext.Provider>
  );
};

export const useImpersonation = () => {
  const context = useContext(ImpersonationContext);
  if (!context) {
    throw new Error("useImpersonation deve ser usado dentro de ImpersonationProvider");
  }
  return context;
};
