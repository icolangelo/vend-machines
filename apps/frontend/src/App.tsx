import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { HashRouter, Route, Routes } from "react-router-dom";
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
import Clients from "./pages/Clients.tsx";
import Products from "./pages/Products.tsx";
import Settings from "./pages/Settings.tsx";
import MachineFormPage from "./pages/MachineFormPage.tsx";
import Telemetry from "./pages/Telemetry.tsx";
import Reports from "./pages/Reports.tsx";
import Alerts from "./pages/Alerts.tsx";
import Admin from "./pages/Admin.tsx";

const queryClient = new QueryClient();

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
                                <Route path="/machines/:id/edit" element={<ProtectedRoute><MachineFormPage /></ProtectedRoute>} />
                                <Route path="/clients" element={<ProtectedRoute><Clients /></ProtectedRoute>} />
                                <Route path="/products" element={<ProtectedRoute><Products /></ProtectedRoute>} />
                                <Route path="/settings" element={<ProtectedRoute><Settings /></ProtectedRoute>} />
                                <Route path="/telemetry" element={<ProtectedRoute><Telemetry /></ProtectedRoute>} />
                                <Route path="/reports" element={<ProtectedRoute><Reports /></ProtectedRoute>} />
                                <Route path="/alerts" element={<ProtectedRoute><Alerts /></ProtectedRoute>} />
                                <Route path="/admin" element={<ProtectedRoute><Admin /></ProtectedRoute>} />
                                {/* ADD ALL CUSTOM ROUTES ABOVE THE CATCH-ALL "*" ROUTE */}
                                <Route path="*" element={<NotFound />} />
                            </Routes>
                        </div>
                    </HashRouter>
                </AuthProvider>
            </TooltipProvider>
        </QueryClientProvider>
    );
};

export default App;
