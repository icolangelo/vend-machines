import { describe, expect, it } from "vitest";
import { getTelemetryEventLabel } from "@/lib/telemetryEvents";

describe("getTelemetryEventLabel", () => {
    it("shows connection events in Portuguese", () => {
        expect(getTelemetryEventLabel("device.connected")).toBe("Máquina conectada");
        expect(getTelemetryEventLabel("device.disconnected")).toBe("Máquina desconectada");
    });

    it("does not expose unknown internal event codes", () => {
        expect(getTelemetryEventLabel("unknown.secret_event")).toBe("Outro evento de telemetria");
    });
});
