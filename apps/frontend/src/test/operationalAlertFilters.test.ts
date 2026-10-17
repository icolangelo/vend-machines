import { describe, expect, it } from "vitest";
import type { OperationalAlertItem } from "@/lib/api";
import { filterOperationalAlerts } from "@/lib/operationalAlertFilters";

const alerts: OperationalAlertItem[] = [
    {
        id: "offline-active", machineId: "machine-1", machineName: "Máquina Central", clientName: "Hospital",
        type: "TelemetryOffline", severity: "critical", status: "Active", title: "Máquina offline",
        description: "Sem comunicação", occurredAt: "2026-09-27T10:00:00Z", updatedAt: "2026-09-27T10:00:00Z", isRead: false
    },
    {
        id: "stock-active", machineId: "machine-2", machineName: "Máquina Recepção", clientName: "Hospital",
        type: "LowStock", severity: "warning", status: "Active", title: "Estoque baixo",
        description: "Reposição necessária", occurredAt: "2026-09-27T10:00:00Z", updatedAt: "2026-09-27T10:00:00Z", isRead: false
    },
    {
        id: "offline-resolved", machineId: "machine-3", machineName: "Máquina Antiga", clientName: "Hospital",
        type: "TelemetryOffline", severity: "critical", status: "Resolved", title: "Máquina offline",
        description: "Conexão restabelecida", occurredAt: "2026-09-26T10:00:00Z", updatedAt: "2026-09-27T10:00:00Z",
        resolvedAt: "2026-09-27T10:00:00Z", isRead: true
    }
];

describe("filterOperationalAlerts", () => {
    it("includes active and resolved alerts in the all filter", () => {
        expect(filterOperationalAlerts(alerts, "", "all").map(alert => alert.id))
            .toEqual(["offline-active", "stock-active", "offline-resolved"]);
    });

    it("limits offline and warning filters to active alerts of the selected type", () => {
        expect(filterOperationalAlerts(alerts, "", "offline").map(alert => alert.id)).toEqual(["offline-active"]);
        expect(filterOperationalAlerts(alerts, "", "warning").map(alert => alert.id)).toEqual(["stock-active"]);
    });

    it("shows only resolved alerts in the history filter", () => {
        expect(filterOperationalAlerts(alerts, "", "resolved").map(alert => alert.id)).toEqual(["offline-resolved"]);
    });

    it("matches machine and location text without case sensitivity", () => {
        expect(filterOperationalAlerts(alerts, "HOSPITAL", "all")).toHaveLength(3);
        expect(filterOperationalAlerts(alerts, "recepção", "all").map(alert => alert.id)).toEqual(["stock-active"]);
    });
});
