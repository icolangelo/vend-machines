import {
    LayoutDashboard,
    Box,
    Users,
    Package,
    BarChart3,
    Settings,
    Bell,
} from "lucide-react";
import { NavLink } from "@/components/NavLink";
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

const mainItems = [
    { title: "Dashboard", url: "/", icon: LayoutDashboard },
    { title: "Máquinas", url: "/machines", icon: Box },
    { title: "Clientes", url: "/clients", icon: Users },
    { title: "Produtos", url: "/products", icon: Package },
    { title: "Relatórios", url: "/relatorios", icon: BarChart3 },
];

const systemItems = [
    { title: "Alertas", url: "/alertas", icon: Bell },
    { title: "Configurações", url: "/settings", icon: Settings },
];

export function AppSidebar() {
    const { state } = useSidebar();
    const collapsed = state === "collapsed";

    return (
        <Sidebar collapsible="icon">
            <SidebarHeader className="px-4 py-5">
                {!collapsed && (
                    <div className="flex items-center gap-2">
                        <div className="h-7 w-7 rounded bg-sidebar-primary flex items-center justify-center">
                            <Box className="h-4 w-4 text-sidebar-primary-foreground" />
                        </div>
                        <span className="text-sm font-semibold text-sidebar-foreground tracking-tight">
                            VendControl
                        </span>
                    </div>
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

            <SidebarFooter className="px-4 py-3">
                {!collapsed && (
                    <p className="text-[0.65rem] text-sidebar-foreground/30">
                        Sistema operacional
                    </p>
                )}
            </SidebarFooter>
        </Sidebar>
    );
}
