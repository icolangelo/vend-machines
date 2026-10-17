import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { HashRouter, Navigate, Route, Routes } from "react-router-dom";
import { lazy, Suspense } from "react";
import { Toaster as Sonner } from "@/components/ui/sonner";
import { Toaster } from "@/components/ui/toaster";
import { TooltipProvider } from "@/components/ui/tooltip";
import { AuthProvider } from "@/contexts/AuthContext";
import { useAuth } from "@/contexts/AuthContext";
import { ImpersonationProvider } from "@/contexts/ImpersonationContext";
import ProtectedRoute from "@/components/ProtectedRoute";
import { CookieConsent } from "@/components/CookieConsent";
const Index = lazy(() => import("./pages/Index.tsx"));
const Login = lazy(() => import("./pages/Login.tsx"));
const NotFound = lazy(() => import("./pages/NotFound.tsx"));
const Machines = lazy(() => import("./pages/Machines.tsx"));
const Locations = lazy(() => import("./pages/Locations.tsx"));
const Products = lazy(() => import("./pages/Products.tsx"));
const Settings = lazy(() => import("./pages/Settings.tsx"));
const Integrations = lazy(() => import("./pages/Integrations.tsx"));
const MachineFormPage = lazy(() => import("./pages/MachineFormPage.tsx"));
const Telemetry = lazy(() => import("./pages/Telemetry.tsx"));
const Reports = lazy(() => import("./pages/Reports.tsx"));
const Alerts = lazy(() => import("./pages/Alerts.tsx"));
const Admin = lazy(() => import("./pages/Admin.tsx"));
const Users = lazy(() => import("./pages/Users.tsx"));
const InviteAcceptance = lazy(() => import("./pages/InviteAcceptance.tsx"));
const ResetPassword = lazy(() => import("./pages/ResetPassword.tsx"));
const VerifyEmail = lazy(() => import("./pages/VerifyEmail.tsx"));
const Inventory = lazy(() => import("./pages/Inventory.tsx"));
const canAccessPaymentSimulator = import.meta.env.DEV || import.meta.env.VITE_IS_TEST_ENVIRONMENT === "true";
const PaymentSimulator = canAccessPaymentSimulator
    ? lazy(() => import("./pages/PaymentSimulator.tsx"))
    : null;

import { useEffect, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import { exchangeOauthCode } from "@/lib/api";
import { useToast } from "@/components/ui/use-toast";
import { RefreshCw } from "lucide-react";

const queryClient = new QueryClient();

const OauthInterceptor = ({ children }: { children: React.ReactNode }) => {
    const navigate = useNavigate();
    const { toast } = useToast();
    const { loading: authLoading, token } = useAuth();
    const [processing, setProcessing] = useState(false);
    const processedCodeRef = useRef<string | null>(null);

    useEffect(() => {
        const params = new URLSearchParams(window.location.search);
        const code = params.get("code");
        const state = params.get("state") || undefined;

        if (code && !authLoading && token && processedCodeRef.current !== code) {
            processedCodeRef.current = code;

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
    }, [authLoading, navigate, toast, token]);

    if (processing) {
        return (
            <div className="fixed inset-0 bg-slate-900/60 backdrop-blur-xs flex flex-col items-center justify-center gap-3 z-50 text-white">
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
                    <ImpersonationProvider>
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
                                <Suspense fallback={<div className="flex min-h-screen items-center justify-center text-sm text-muted-foreground">Carregando página...</div>}>
                                <Routes>
                                    <Route path="/" element={<Login />} />
                                    <Route path="/dashboard" element={<ProtectedRoute><Index /></ProtectedRoute>} />
                                    <Route path="/machines" element={<ProtectedRoute><Machines /></ProtectedRoute>} />
                                    <Route path="/machines/new" element={<ProtectedRoute allowedRoles={["Admin", "PlatformAdmin"]}><MachineFormPage /></ProtectedRoute>} />
                                    <Route path="/machines/edit" element={<ProtectedRoute><Navigate to="/machines" replace /></ProtectedRoute>} />
                                    <Route path="/machines/:id/edit" element={<ProtectedRoute allowedRoles={["Admin", "PlatformAdmin"]}><MachineFormPage /></ProtectedRoute>} />
                                    <Route path="/locations" element={<ProtectedRoute><Locations /></ProtectedRoute>} />
                                    <Route path="/clients" element={<ProtectedRoute><Navigate to="/locations" replace /></ProtectedRoute>} />
                                    <Route path="/products" element={<ProtectedRoute><Products /></ProtectedRoute>} />
                                    <Route path="/inventory" element={<ProtectedRoute><Inventory /></ProtectedRoute>} />
                                    <Route path="/settings" element={<ProtectedRoute><Settings /></ProtectedRoute>} />
                                    <Route path="/integrations" element={<ProtectedRoute><Integrations /></ProtectedRoute>} />
                                    <Route path="/telemetry" element={<ProtectedRoute><Telemetry /></ProtectedRoute>} />
                                    <Route path="/reports" element={<ProtectedRoute><Reports /></ProtectedRoute>} />
                                    <Route path="/alerts" element={<ProtectedRoute><Alerts /></ProtectedRoute>} />
                                    <Route path="/admin" element={<ProtectedRoute><Admin /></ProtectedRoute>} />
                                    <Route path="/users" element={<ProtectedRoute><Users /></ProtectedRoute>} />
                                    <Route path="/invite" element={<InviteAcceptance />} />
                                    <Route path="/reset-password" element={<ResetPassword />} />
                                    <Route path="/verify-email" element={<VerifyEmail />} />
                                    {PaymentSimulator && <Route path="/payment-simulator" element={<ProtectedRoute allowedRoles={["Admin", "PlatformAdmin"]}><PaymentSimulator /></ProtectedRoute>} />}
                                    {/* ADD ALL CUSTOM ROUTES ABOVE THE CATCH-ALL "*" ROUTE */}
                                    <Route path="*" element={<NotFound />} />
                                </Routes>
                                </Suspense>
                            </div>
                        </OauthInterceptor>
                    </HashRouter>
                    </ImpersonationProvider>
                </AuthProvider>
            </TooltipProvider>
        </QueryClientProvider>
    );
};

export default App;
