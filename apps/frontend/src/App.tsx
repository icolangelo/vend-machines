import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { BrowserRouter, Route, Routes } from "react-router-dom";
import { Toaster as Sonner } from "@/components/ui/sonner";
import { Toaster } from "@/components/ui/toaster";
import { TooltipProvider } from "@/components/ui/tooltip";
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

const queryClient = new QueryClient();

const App = () => {
    const isTestEnv = import.meta.env.VITE_IS_TEST_ENVIRONMENT !== "false";

    return (
        <QueryClientProvider client={queryClient}>
            <TooltipProvider>
                <Toaster />
                <Sonner />
                <BrowserRouter basename={import.meta.env.BASE_URL}>
                    <div className={isTestEnv ? "is-test-env" : ""}>
                        {isTestEnv && (
                            <div className="ambiente-teste-banner">
                                Ambiente de Teste
                            </div>
                        )}
                        <Routes>
                            <Route path="/" element={<Login />} />
                            <Route path="/dashboard" element={<Index />} />
                            <Route path="/machines" element={<Machines />} />
                            <Route path="/machines/new" element={<MachineFormPage />} />
                            <Route path="/machines/:id/edit" element={<MachineFormPage />} />
                            <Route path="/clients" element={<Clients />} />
                            <Route path="/products" element={<Products />} />
                            <Route path="/settings" element={<Settings />} />
                            <Route path="/telemetry" element={<Telemetry />} />
                            <Route path="/reports" element={<Reports />} />
                            <Route path="/alerts" element={<Alerts />} />
                            {/* ADD ALL CUSTOM ROUTES ABOVE THE CATCH-ALL "*" ROUTE */}
                            <Route path="*" element={<NotFound />} />
                        </Routes>
                    </div>
                </BrowserRouter>
            </TooltipProvider>
        </QueryClientProvider>
    );
};

export default App;
