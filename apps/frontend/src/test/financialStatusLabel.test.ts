import { describe, expect, it } from "vitest";
import { financialStatusLabel } from "@/lib/financialStatusLabel";

describe("financialStatusLabel", () => {
    it("translates known financial states into Portuguese", () => {
        expect(financialStatusLabel("Settled")).toBe("Liquidada");
        expect(financialStatusLabel("PartiallyRefunded")).toBe("Parcialmente estornada");
        expect(financialStatusLabel("ReconciliationRequired")).toBe("Conciliação necessária");
    });

    it("does not expose an unknown internal status", () => {
        expect(financialStatusLabel("NewInternalState")).toBe("Desconhecida");
    });
});
