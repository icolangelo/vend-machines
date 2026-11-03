import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { join, resolve, sep } from "node:path";
import { expect, it } from "vitest";

const forbiddenFixtureMarkers = [
    "test-session-closure",
    "vm-test-closure-alert-evidence",
    "Encerramento físico pendente",
];

it.each(forbiddenFixtureMarkers)("rejeita bundle que contém marcador de teste: %s", marker => {
    const workspaceRoot = resolve(process.cwd());
    const temporaryRoot = join(workspaceRoot, ".tmp");
    mkdirSync(temporaryRoot, { recursive: true });
    const resolvedTemporaryRoot = realpathSync(temporaryRoot);
    if (!resolvedTemporaryRoot.startsWith(`${workspaceRoot}${sep}`)) {
        throw new Error("O diretório temporário do teste saiu da raiz do frontend.");
    }

    const fixtureRoot = mkdtempSync(join(resolvedTemporaryRoot, "production-bundle-guard-"));
    const resolvedFixtureRoot = realpathSync(fixtureRoot);
    if (!resolvedFixtureRoot.startsWith(`${resolvedTemporaryRoot}${sep}`)) {
        throw new Error("A fixture do teste saiu do diretório temporário do frontend.");
    }

    try {
        const assetsDirectory = join(resolvedFixtureRoot, "assets");
        mkdirSync(assetsDirectory);
        writeFileSync(join(assetsDirectory, "bundle.js"), `synthetic bundle marker: ${marker}`, "utf8");

        const checkerPath = join(workspaceRoot, "scripts", "assert-production-bundle.mjs");
        const result = spawnSync(process.execPath, [checkerPath, resolvedFixtureRoot], { encoding: "utf8" });
        expect(result.status).toBe(1);
        expect(`${result.stdout}${result.stderr}`).toContain("O bundle de produção contém código ou dados exclusivos de teste:");
    } finally {
        rmSync(resolvedFixtureRoot, { recursive: true, force: true });
    }
});
