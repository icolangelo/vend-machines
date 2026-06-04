import { type Machine } from "@/data/mockData";
import { StatusIndicator } from "./StatusIndicator";
import { AlertTriangle } from "lucide-react";

export function MachineAlertTable({ machines }: { machines: Machine[] }) {
    const alertMachines = machines.filter(
        (m) => m.status === "offline" || m.status === "warning" || m.stockLevel < 25
    );

    return (
        <div className="bg-card border rounded-lg animate-slide-up" style={{ animationDelay: "100ms" }}>
            <div className="px-5 py-4 border-b flex items-center gap-2">
                <AlertTriangle className="h-4 w-4 text-warning" />
                <h2 className="text-sm font-semibold text-foreground">
                    Máquinas que requerem atenção
                </h2>
                <span className="ml-auto font-mono-data text-xs text-muted-foreground">
                    {alertMachines.length} itens
                </span>
            </div>
            <div className="overflow-x-auto">
                <table className="w-full text-sm">
                    <thead>
                        <tr className="border-b bg-muted/50">
                            <th className="text-left px-5 py-2.5 text-label font-medium">ID</th>
                            <th className="text-left px-5 py-2.5 text-label font-medium">Máquina</th>
                            <th className="text-left px-5 py-2.5 text-label font-medium">Cliente</th>
                            <th className="text-left px-5 py-2.5 text-label font-medium">Status</th>
                            <th className="text-left px-5 py-2.5 text-label font-medium">Estoque</th>
                            <th className="text-left px-5 py-2.5 text-label font-medium">Última Sinc.</th>
                        </tr>
                    </thead>
                    <tbody>
                        {alertMachines.map((machine) => (
                            <tr
                                key={machine.id}
                                className="border-b last:border-b-0 hover:bg-muted/30 transition-colors duration-150 cursor-pointer"
                            >
                                <td className="px-5 py-3 font-mono-data text-xs text-muted-foreground">
                                    {machine.id}
                                </td>
                                <td className="px-5 py-3 font-medium text-foreground">
                                    {machine.name}
                                </td>
                                <td className="px-5 py-3 text-muted-foreground">
                                    {machine.clientName}
                                </td>
                                <td className="px-5 py-3">
                                    <StatusIndicator status={machine.status} />
                                </td>
                                <td className="px-5 py-3">
                                    <div className="flex items-center gap-2">
                                        <div className="w-16 h-1.5 bg-muted rounded-full overflow-hidden">
                                            <div
                                                className={`h-full rounded-full ${machine.stockLevel > 50
                                                        ? "bg-success"
                                                        : machine.stockLevel > 20
                                                            ? "bg-warning"
                                                            : "bg-destructive"
                                                    }`}
                                                style={{ width: `${machine.stockLevel}%` }}
                                            />
                                        </div>
                                        <span className="font-mono-data text-xs">{machine.stockLevel}%</span>
                                    </div>
                                </td>
                                <td className="px-5 py-3 text-xs text-muted-foreground">
                                    {machine.lastSync}
                                </td>
                            </tr>
                        ))}
                    </tbody>
                </table>
            </div>
        </div>
    );
}
