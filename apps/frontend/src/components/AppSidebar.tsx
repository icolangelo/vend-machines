import React, { useState } from "react";
import {
    LayoutDashboard,
    Box,
    MapPin,
    Package,
    BarChart3,
    Settings,
    Bell,
    Terminal,
    Shield,
    QrCode,
    CreditCard,
    Blocks,
    Eye,
} from "lucide-react";
import { NavLink } from "@/components/NavLink";
import { useAuth } from "@/contexts/AuthContext";
import { useImpersonation } from "@/contexts/ImpersonationContext";
import { CompanySwitcherModal } from "@/components/CompanySwitcherModal";
import {
    Sidebar,
    SidebarContent,
    SidebarGroup,
    SidebarGroupContent,
    SidebarGroupLabel,
    SidebarMenu,
    SidebarMenuButton,
    SidebarMenuItem,
    SidebarHeader,
    SidebarFooter,
    useSidebar,
} from "@/components/ui/sidebar";
import { PrivacyPolicyModal } from "@/components/PrivacyPolicyModal";

const mainItems = [
    { title: "Dashboard", url: "/", icon: LayoutDashboard },
    { title: "Maquinas", url: "/machines", icon: Box },
    { title: "Localizacoes", url: "/locations", icon: MapPin },
    { title: "Produtos", url: "/products", icon: Package },
    { title: "Relatorios", url: "/reports", icon: BarChart3 },
];

export function AppSidebar() {
    const state = useSidebar().state;
    const collapsed = state === "collapsed";
    const { user } = useAuth();
    const { isSuperAdmin, impersonatedCompany, effectiveCompanyName } = useImpersonation();
    const [isPolicyOpen, setIsPolicyOpen] = useState(false);
    const [isSwitcherOpen, setIsSwitcherOpen] = useState(false);
    const [clickCount, setClickCount] = useState(0);
    const clickTimerRef = React.useRef<ReturnType<typeof setTimeout> | null>(null);

    const handleCompanyBadgeClick = () => {
        if (!isSuperAdmin) return;
        setClickCount((prev) => {
            const next = prev + 1;
            if (next === 2) {
                setIsSwitcherOpen(true);
                if (clickTimerRef.current) clearTimeout(clickTimerRef.current);
                setClickCount(0);
                return 0;
            }
            if (clickTimerRef.current) clearTimeout(clickTimerRef.current);
            clickTimerRef.current = setTimeout(() => setClickCount(0), 400);
            return next;
        });
    };

    const systemItems = [
        { title: "Alertas", url: "/alerts", icon: Bell },
        { title: "Telemetria (MDB)", url: "/telemetry", icon: Terminal },
        { title: "Simulador Pix", url: "/payment-simulator", icon: QrCode },
        ...(isSuperAdmin ? [{ title: "Admin", url: "/admin", icon: Shield }] : []),
        { title: "Integracoes", url: "/integrations", icon: Blocks },
        { title: "Configuracoes", url: "/settings", icon: Settings },
    ];

    return (
        <Sidebar collapsible="icon">
            <SidebarHeader className="px-4 py-5 flex flex-col gap-1.5">
                {!collapsed && (
                    <>
                        <div className="flex items-center gap-2">
                            <div className="h-7 w-7 rounded bg-sidebar-primary flex items-center justify-center">
                                <Box className="h-4 w-4 text-sidebar-primary-foreground" />
                            </div>
                            <span className="text-sm font-semibold text-sidebar-foreground tracking-tight">
                                VendControl
                            </span>
                        </div>

                        {/* Company badge */}
                        {effectiveCompanyName && (
                            isSuperAdmin ? (
                                <button
                                    onClick={handleCompanyBadgeClick}
                                    title="Duplo clique para trocar empresa"
                                    className={`flex items-center gap-1 text-[10px] font-bold px-2 py-0.5 rounded uppercase tracking-wider select-none transition-all duration-200 w-fit
                                        ${impersonatedCompany
                                            ? "text-amber-700 bg-amber-50 border border-amber-300 hover:bg-amber-100"
                                            : "text-blue-600 bg-blue-50/70 border border-blue-200/50 hover:bg-blue-100/70"
                                        }`}
                                >
                                    {impersonatedCompany && (
                                        <Eye className="h-2.5 w-2.5 shrink-0" />
                                    )}
                                    <span className="max-w-[140px] truncate">{effectiveCompanyName}</span>
                                </button>
                            ) : (
                                <span className="text-[10px] font-bold text-blue-600 bg-blue-50/70 border border-blue-200/50 px-2 py-0.5 rounded w-fit uppercase tracking-wider select-none">
                                    {effectiveCompanyName}
                                </span>
                            )
                        )}
                    </>
                )}
                {collapsed && (
                    <div className="flex justify-center">
                        <div className="h-7 w-7 rounded bg-sidebar-primary flex items-center justify-center">
                            <Box className="h-4 w-4 text-sidebar-primary-foreground" />
                        </div>
                    </div>
                )}
            </SidebarHeader>

            <SidebarContent>
                <SidebarGroup>
                    <SidebarGroupLabel className="text-sidebar-foreground/40 text-[0.65rem] tracking-widest uppercase">
                        Principal
                    </SidebarGroupLabel>
                    <SidebarGroupContent>
                        <SidebarMenu>
                            {mainItems.map((item) => (
                                <SidebarMenuItem key={item.title}>
                                    <SidebarMenuButton asChild>
                                        <NavLink
                                            to={item.url}
                                            end={item.url === "/"}
                                            className="text-sidebar-foreground/70 hover:text-sidebar-foreground hover:bg-sidebar-accent transition-colors duration-150"
                                            activeClassName="text-sidebar-primary-foreground bg-sidebar-accent border-l-2 border-sidebar-primary"
                                        >
                                            <item.icon className="h-4 w-4 shrink-0" />
                                            {!collapsed && <span className="text-sm">{item.title}</span>}
                                        </NavLink>
                                    </SidebarMenuButton>
                                </SidebarMenuItem>
                            ))}
                        </SidebarMenu>
                    </SidebarGroupContent>
                </SidebarGroup>

                <SidebarGroup>
                    <SidebarGroupLabel className="text-sidebar-foreground/40 text-[0.65rem] tracking-widest uppercase">
                        Sistema
                    </SidebarGroupLabel>
                    <SidebarGroupContent>
                        <SidebarMenu>
                            {systemItems.map((item) => (
                                <SidebarMenuItem key={item.title}>
                                    <SidebarMenuButton asChild>
                                        <NavLink
                                            to={item.url}
                                            className="text-sidebar-foreground/70 hover:text-sidebar-foreground hover:bg-sidebar-accent transition-colors duration-150"
                                            activeClassName="text-sidebar-primary-foreground bg-sidebar-accent border-l-2 border-sidebar-primary"
                                        >
                                            <item.icon className="h-4 w-4 shrink-0" />
                                            {!collapsed && <span className="text-sm">{item.title}</span>}
                                        </NavLink>
                                    </SidebarMenuButton>
                                </SidebarMenuItem>
                            ))}
                        </SidebarMenu>
                    </SidebarGroupContent>
                </SidebarGroup>
            </SidebarContent>

            <SidebarFooter className="px-4 py-3 flex flex-col gap-2">
                {!collapsed && (
                    <>
                        <button
                            onClick={() => setIsPolicyOpen(true)}
                            className="text-[0.7rem] text-sidebar-foreground/50 hover:text-sidebar-foreground/80 hover:underline transition-colors flex items-center gap-1 w-full text-left bg-transparent border-0 p-0 cursor-pointer font-sans"
                        >
                            Politica de Privacidade
                        </button>
                        <p className="text-[0.65rem] text-sidebar-foreground/30">
                            Sistema operacional
                        </p>
                    </>
                )}
                <PrivacyPolicyModal isOpen={isPolicyOpen} onClose={() => setIsPolicyOpen(false)} />
            </SidebarFooter>

            <CompanySwitcherModal isOpen={isSwitcherOpen} onClose={() => setIsSwitcherOpen(false)} />
        </Sidebar>
    );
}
