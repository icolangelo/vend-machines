import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { HashRouter, Navigate, Route, Routes } from "react-router-dom";
import { Toaster as Sonner } from "@/components/ui/sonner";
import { Toaster } from "@/components/ui/toaster";
import { TooltipProvider } from "@/components/ui/tooltip";
import { AuthProvider } from "@/contexts/AuthContext";
import ProtectedRoute from "@/components/ProtectedRoute";
import { CookieConsent } from "@/components/CookieConsent";
import Index from "./pages/Index.tsx";
import Login from "./pages/Login.tsx";
import NotFound from "./pages/NotFound.tsx";
import Machines from "./pages/Machines.tsx";
import Locations from "./pages/Locations.tsx";
import Products from "./pages/Products.tsx";
import Settings from "./pages/Settings.tsx";
import Integrations from "./pages/Integrations.tsx";
import MachineFormPage from "./pages/MachineFormPage.tsx";
import Telemetry from "./pages/Telemetry.tsx";
import Reports from "./pages/Reports.tsx";
import Alerts from "./pages/Alerts.tsx";
import Admin from "./pages/Admin.tsx";
import PaymentSimulator from "./pages/PaymentSimulator.tsx";

import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { exchangeOauthCode } from "@/lib/api";
import { useToast } from "@/components/ui/use-toast";
import { RefreshCw } from "lucide-react";

const queryClient = new QueryClient();

const OauthInterceptor = ({ children }: { children: React.ReactNode }) => {
    const navigate = useNavigate();
    const { toast } = useToast();
    const [processing, setProcessing] = useState(false);

    useEffect(() => {
        const params = new URLSearchParams(window.location.search);
        const code = params.get("code");
        const state = params.get("state") || undefined;

        if (code) {
            const token = sessionStorage.getItem("token") || localStorage.getItem("token");
            if (!token) return;

            setProcessing(true);
            const redirectUri = window.location.origin + "/";

            exchangeOauthCode(code, redirectUri, state)
                .then(() => {
                    toast({
                        title: "Integração Conectada",
                        description: "Sua conta do Mercado Pago foi integrada com sucesso!"
                    });
                    
                    const url = new URL(window.location.href);
                    url.search = "";
                    window.history.replaceState({}, document.title, url.toString());
                    navigate("/integrations");
                })
                .catch((err) => {
                    console.error("Erro no callback do OAuth:", err);
                    toast({
                        title: "Erro na Integração",
                        description: err.message || "Não foi possível vincular sua conta.",
                        variant: "destructive"
                    });
                    const url = new URL(window.location.href);
                    url.search = "";
                    window.history.replaceState({}, document.title, url.toString());
                })
                .finally(() => {
                    setProcessing(false);
                });
        }
    }, [navigate, toast]);

    if (processing) {
        return (
            <div className="fixed inset-0 bg-slate-900/60 backdrop-blur-sm flex flex-col items-center justify-center gap-3 z-50 text-white">
                <RefreshCw className="w-10 h-10 text-sky-400 animate-spin" />
                <p className="font-semibold text-lg">Conectando sua conta Mercado Pago...</p>
                <p className="text-xs text-slate-300">Por favor, aguarde a sincronização.</p>
            </div>
        );
    }

    return <>{children}</>;
};

const App = () => {
    const isTestEnv = import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true";

    return (
        <QueryClientProvider client={queryClient}>
            <TooltipProvider>
                <AuthProvider>
                    <Toaster />
                    <Sonner />
                    <CookieConsent />
                    <HashRouter>
                        <OauthInterceptor>
                            <div className={isTestEnv ? "is-test-env" : ""}>
                                {isTestEnv && (
                                    <div className="ambiente-teste-banner">
                                        Ambiente de Teste
                                    </div>
                                )}
                                <Routes>
                                    <Route path="/" element={<Login />} />
                                    <Route path="/dashboard" element={<ProtectedRoute><Index /></ProtectedRoute>} />
                                    <Route path="/machines" element={<ProtectedRoute><Machines /></ProtectedRoute>} />
                                    <Route path="/machines/new" element={<ProtectedRoute><MachineFormPage /></ProtectedRoute>} />
                                    <Route path="/machines/edit" element={<ProtectedRoute><Navigate to="/machines" replace /></ProtectedRoute>} />
                                    <Route path="/machines/:id/edit" element={<ProtectedRoute><MachineFormPage /></ProtectedRoute>} />
                                    <Route path="/locations" element={<ProtectedRoute><Locations /></ProtectedRoute>} />
                                    <Route path="/clients" element={<ProtectedRoute><Navigate to="/locations" replace /></ProtectedRoute>} />
                                    <Route path="/products" element={<ProtectedRoute><Products /></ProtectedRoute>} />
                                    <Route path="/settings" element={<ProtectedRoute><Settings /></ProtectedRoute>} />
                                    <Route path="/integrations" element={<ProtectedRoute><Integrations /></ProtectedRoute>} />
                                    <Route path="/telemetry" element={<ProtectedRoute><Telemetry /></ProtectedRoute>} />
                                    <Route path="/reports" element={<ProtectedRoute><Reports /></ProtectedRoute>} />
                                    <Route path="/alerts" element={<ProtectedRoute><Alerts /></ProtectedRoute>} />
                                    <Route path="/admin" element={<ProtectedRoute><Admin /></ProtectedRoute>} />
                                    <Route path="/payment-simulator" element={<ProtectedRoute><PaymentSimulator /></ProtectedRoute>} />
                                    {/* ADD ALL CUSTOM ROUTES ABOVE THE CATCH-ALL "*" ROUTE */}
                                    <Route path="*" element={<NotFound />} />
                                </Routes>
                            </div>
                        </OauthInterceptor>
                    </HashRouter>
                </AuthProvider>
            </TooltipProvider>
        </QueryClientProvider>
    );
};

export default App;
