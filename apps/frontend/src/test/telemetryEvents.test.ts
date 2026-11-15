import { describe, expect, it } from "vitest";
import { getTelemetryEventLabel } from "@/lib/telemetryEvents";

describe("getTelemetryEventLabel", () => {
    it("shows connection events in Portuguese", () => {
        expect(getTelemetryEventLabel("device.connected")).toBe("Máquina conectada");
        expect(getTelemetryEventLabel("device.disconnected")).toBe("Máquina desconectada");
    });

    it("shows the original code for events without a Portuguese label", () => {
        expect(getTelemetryEventLabel("unknown.secret_event")).toBe("Evento não catalogado: unknown.secret_event");
    });
});
