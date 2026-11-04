import { createContext } from "react";

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

export const ImpersonationContext = createContext<ImpersonationContextType | undefined>(undefined);
