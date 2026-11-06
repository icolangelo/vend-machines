import { useContext } from "react";
import { ImpersonationContext } from "@/contexts/impersonationContextDefinition";

export const useImpersonation = () => {
  const context = useContext(ImpersonationContext);
  if (!context) {
    throw new Error("useImpersonation deve ser usado dentro de ImpersonationProvider");
  }
  return context;
};
