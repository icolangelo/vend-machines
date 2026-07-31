import { describe, expect, it } from "vitest";
import { getMdbStatusDetail, MDB_STATUS_DETAILS } from "@/lib/mdbStatus";

describe("status MDB", () => {
    it.each([
        "inactive_state",
        "disable_state",
        "enabled_state",
        "idle_state",
        "vend_state",
    ])("possui texto em português para %s", status => {
        expect(MDB_STATUS_DETAILS[status]?.label).toBeTruthy();
        expect(MDB_STATUS_DETAILS[status]?.description).toBeTruthy();
    });

    it("trata status ausente ou desconhecido sem liberar a máquina", () => {
        expect(getMdbStatusDetail(null).label).toBe("Status MDB desconhecido");
        expect(getMdbStatusDetail("outro_estado").label).toBe("Status MDB desconhecido");
    });
});
