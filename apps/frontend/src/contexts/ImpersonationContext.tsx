import React, { useState, useEffect, useCallback } from "react";
import { useAuth } from "@/contexts/useAuth";
import { ImpersonationContext, type ImpersonatedCompany } from "@/contexts/impersonationContextDefinition";

const STORAGE_KEY = "impersonatedCompany";

export const ImpersonationProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const { user, loading } = useAuth();
  const isSuperAdmin = user?.role === "PlatformAdmin";

  const [impersonatedCompany, setImpersonatedCompanyState] = useState<ImpersonatedCompany | null>(() => {
    try {
      const stored = sessionStorage.getItem(STORAGE_KEY);
      return stored ? JSON.parse(stored) : null;
    } catch {
      return null;
    }
  });

  // Clear impersonation only after auth finishes loading and user logged out
  useEffect(() => {
    if (!loading && !user) {
      sessionStorage.removeItem(STORAGE_KEY);
      setImpersonatedCompanyState(null);
    }
  }, [loading, user]);

  // Safety net: clear impersonation if confirmed non-SuperAdmin (wait for auth to finish loading)
  useEffect(() => {
    if (!loading && !isSuperAdmin && impersonatedCompany) {
      sessionStorage.removeItem(STORAGE_KEY);
      setImpersonatedCompanyState(null);
    }
  }, [loading, isSuperAdmin, impersonatedCompany]);

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
