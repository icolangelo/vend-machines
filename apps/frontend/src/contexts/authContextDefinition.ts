import { createContext } from "react";
import type { AuthUser } from "@/lib/authClient";

export type User = AuthUser;

export interface AuthContextType {
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

export const AuthContext = createContext<AuthContextType | undefined>(undefined);
