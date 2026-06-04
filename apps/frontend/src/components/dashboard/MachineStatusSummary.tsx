import { type Machine } from "@/data/mockData";
import { CheckCircle2, AlertTriangle, XCircle, Package } from "lucide-react";

export function MachineStatusSummary({ machines }: { machines: Machine[] }) {
    const online = machines.filter((m) => m.status === "online").length;
    const warning = machines.filter((m) => m.status === "warning").length;
    const offline = machines.filter((m) => m.status === "offline").length;
    const lowStock = machines.filter((m) => m.stockLevel < 25 && m.stockLevel > 0).length;

    const items = [
        { label: "Online", count: online, icon: CheckCircle2, colorClass: "text-success" },
        { label: "Atenção", count: warning, icon: AlertTriangle, colorClass: "text-warning" },
        { label: "Offline", count: offline, icon: XCircle, colorClass: "text-destructive" },
        { label: "Estoque Baixo", count: lowStock, icon: Package, colorClass: "text-warning" },
    ];

    return (
        <div className="bg-card border rounded-lg p-5 animate-slide-up" style={{ animationDelay: "50ms" }}>
            <h2 className="text-label mb-4">Status da Frota</h2>
            <div className="grid grid-cols-2 gap-4">
                {items.map((item) => (
                    <div key={item.label} className="flex items-center gap-3">
                        <item.icon className={`h-5 w-5 ${item.colorClass}`} />
                        <div>
                            <p className="font-mono-data text-lg font-medium text-foreground leading-none">
                                {item.count}
                            </p>
                            <p className="text-xs text-muted-foreground mt-0.5">{item.label}</p>
                        </div>
                    </div>
                ))}
            </div>
            <div className="mt-4 pt-3 border-t">
                <div className="flex items-center justify-between">
                    <span className="text-xs text-muted-foreground">Total de máquinas</span>
                    <span className="font-mono-data text-sm font-medium">{machines.length}</span>
                </div>
            </div>
        </div>
    );
}
