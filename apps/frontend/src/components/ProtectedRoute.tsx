import React from "react";
import { Navigate } from "react-router-dom";
import { useAuth } from "@/contexts/AuthContext";
import { Loader2 } from "lucide-react";

interface ProtectedRouteProps {
  children: React.ReactNode;
}

const ProtectedRoute: React.FC<ProtectedRouteProps> = ({ children }) => {
  const { token, loading } = useAuth();

  if (loading) {
    return (
      <div className="flex flex-col items-center justify-center min-h-screen bg-slate-50/50">
        <div className="relative flex items-center justify-center">
          {/* Micro-animação de ondas em pulso */}
          <div className="absolute w-16 h-16 bg-blue-500/10 rounded-full animate-ping" />
          <Loader2 className="w-10 h-10 text-blue-600 animate-spin relative z-10" />
        </div>
        <p className="mt-4 text-sm font-medium text-slate-500 animate-pulse font-sans">
          Carregando informações seguras...
        </p>
      </div>
    );
  }

  if (!token) {
    return <Navigate to="/" replace />;
  }

  return <>{children}</>;
};

export default ProtectedRoute;
