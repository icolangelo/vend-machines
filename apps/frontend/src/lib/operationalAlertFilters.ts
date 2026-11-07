import type { OperationalAlertItem } from "@/lib/api";

export type OperationalAlertFilter = "all" | "offline" | "warning" | "resolved";

export function filterOperationalAlerts(
    alerts: OperationalAlertItem[],
    search: string,
    statusFilter: OperationalAlertFilter
): OperationalAlertItem[] {
    const normalizedSearch = search.trim().toLocaleLowerCase("pt-BR");

    return alerts.filter(alert => {
        const matchesSearch = !normalizedSearch ||
            (alert.machineName ?? "").toLocaleLowerCase("pt-BR").includes(normalizedSearch) ||
            (alert.clientName ?? "").toLocaleLowerCase("pt-BR").includes(normalizedSearch) ||
            (alert.machineId ?? "").toLocaleLowerCase("pt-BR").includes(normalizedSearch) ||
            `${alert.title} ${alert.description}`.toLocaleLowerCase("pt-BR").includes(normalizedSearch);

        const matchesStatus =
            statusFilter === "all" ||
            (statusFilter === "warning" && alert.status === "Active" && alert.type !== "TelemetryOffline") ||
            (statusFilter === "offline" && alert.status === "Active" && alert.type === "TelemetryOffline") ||
            (statusFilter === "resolved" && alert.status === "Resolved");

        return matchesSearch && matchesStatus;
    });
}
