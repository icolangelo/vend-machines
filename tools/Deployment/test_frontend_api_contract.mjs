#!/usr/bin/env node

import assert from "node:assert/strict";
import test from "node:test";
import { mkdirSync, mkdtempSync, readFileSync, rmdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { execFileSync } from "node:child_process";
import {
  findContractMismatches,
  findNewContractMismatches,
  loadFrontendSourcesFromGitRef,
  loadFrontendSourcesFromWorkingTree,
  materializeControllersFromGitRef,
  parseFrontendRequestEntries,
  parseFrontendRequests,
  referencedClientFunctions,
} from "./CheckFrontendApiContract.mjs";

test("extracts route, dynamic identifiers, query strings and HTTP methods from frontend calls", () => {
  const source = `
const base = "/api";
authFetch(\`\${API_BASE_URL}/machines/\${encodeURIComponent(id)}?page=1\`, { method: "PUT" });
fetch(\`\${API_BASE_URL}/alerts/summary\`);
`;
  assert.deepEqual(
    [...parseFrontendRequests(source)].sort(),
    ["GET /alerts/summary", "PUT /machines/{}"],
  );
});

test("expands conditional API paths into every concrete route", () => {
  const source = 'fetch(`${API_BASE_URL}/auth/${isReset ? "reset-password" : "request-password-reset"}`, { method: "POST" });';
  assert.deepEqual(
    [...parseFrontendRequests(source)].sort(),
    ["POST /auth/request-password-reset", "POST /auth/reset-password"],
  );
});

test("checks API requests made directly from production pages", () => {
  const directory = mkdtempSync(join(tmpdir(), "frontend-api-contract-page-"));
  try {
    writeFileSync(join(directory, "AuthController.cs"), `
      [Route("api/[controller]")]
      public class AuthController : ControllerBase
      {
        [HttpPost("reset-password")] public IActionResult Reset() => Ok();
      }
    `);
    const mismatches = findContractMismatches([
      {
        filename: "apps/frontend/src/pages/PasswordRecovery.tsx",
        text: 'export function PasswordRecovery() { return fetch(`${API_BASE_URL}/auth/${isReset ? "reset-password" : "request-password-reset"}`, { method: "POST" }); }',
      },
    ], directory, new Set());
    assert.deepEqual(mismatches, ["POST /auth/request-password-reset"]);
  } finally {
    rmSync(join(directory, "AuthController.cs"), { force: true });
    rmdirSync(directory);
  }
});

test("associates API calls with their exported helper for live-route filtering", () => {
  const entries = parseFrontendRequestEntries(
    'export async function saveIntegration() { return authFetch(`${API_BASE_URL}/payments/integration`, { method: "POST" }); }',
    "apps/frontend/src/lib/api.ts",
  ).entries;
  assert.deepEqual(entries, [{ request: "POST /payments/integration", owner: "saveIntegration" }]);
});

test("requires matching HTTP verbs, not merely a matching path", () => {
  const directory = mkdtempSync(join(tmpdir(), "frontend-api-contract-"));
  try {
    writeFileSync(join(directory, "MachinesController.cs"), `
      [Route("api/[controller]")]
      public class MachinesController : ControllerBase
      {
        [HttpGet("{id}")] public IActionResult Get(string id) => Ok();
      }
    `);
    const mismatches = findContractMismatches([
      { filename: "api.ts", text: "authFetch(`${API_BASE_URL}/machines/${id}`, { method: \"PUT\" });" },
    ], directory);
    assert.deepEqual(mismatches, ["PUT /machines/{}"]);
  } finally {
    rmSync(join(directory, "MachinesController.cs"), { force: true });
    rmdirSync(directory);
  }
});

test("accepts default GET and POST routes from the session helper", () => {
  const directory = mkdtempSync(join(tmpdir(), "frontend-api-contract-"));
  try {
    writeFileSync(join(directory, "AuthController.cs"), `
      [Route("api/[controller]")]
      public class AuthController : ControllerBase
      {
        [HttpGet("me")] public IActionResult Me() => Ok();
        [HttpPost("login")] public IActionResult Login() => Ok();
      }
    `);
    const mismatches = findContractMismatches([
      { filename: "authClient.ts", text: "authFetch(`${API_BASE_URL}/auth/me`); postAuthSession(\"login\", {});" },
    ], directory);
    assert.deepEqual(mismatches, []);
  } finally {
    rmSync(join(directory, "AuthController.cs"), { force: true });
    rmdirSync(directory);
  }
});

test("loads API controllers from a selected git ref without changing the checkout", () => {
  const repository = mkdtempSync(join(tmpdir(), "frontend-api-contract-ref-"));
  try {
    execFileSync("git", ["init", "--quiet"], { cwd: repository });
    const controllers = join(repository, "Controllers");
    mkdirSync(controllers);
    writeFileSync(join(controllers, "MachinesController.cs"), `
      [Route("api/[controller]")]
      public class MachinesController : ControllerBase
      {
        [HttpGet("{id}")] public IActionResult Get(string id) => Ok();
      }
    `);
    execFileSync("git", ["add", "Controllers/MachinesController.cs"], { cwd: repository });
    execFileSync("git", [
      "-c", "user.name=Contract Test",
      "-c", "user.email=contract@example.invalid",
      "commit", "--quiet", "-m", "add controller",
    ], { cwd: repository });

    const materialized = materializeControllersFromGitRef("HEAD", repository, "Controllers");
    try {
      assert.equal(
        readFileSync(join(materialized, "MachinesController.cs"), "utf8"),
        readFileSync(join(controllers, "MachinesController.cs"), "utf8"),
      );
    } finally {
      rmSync(materialized, { recursive: true, force: true });
    }
  } finally {
    rmSync(repository, { recursive: true, force: true });
  }
});

test("loads frontend API clients from a selected git ref without changing the checkout", () => {
  const repository = mkdtempSync(join(tmpdir(), "frontend-client-contract-ref-"));
  try {
    execFileSync("git", ["init", "--quiet"], { cwd: repository });
    const clientDirectory = join(repository, "apps", "frontend", "src", "lib");
    mkdirSync(clientDirectory, { recursive: true });
    const apiPath = join(clientDirectory, "api.ts");
    const authPath = join(clientDirectory, "authClient.ts");
    writeFileSync(apiPath, "fetch(`${API_BASE_URL}/machines`);");
    writeFileSync(authPath, "postAuthSession(\"login\", {});");
    execFileSync("git", ["add", "apps/frontend/src/lib/api.ts", "apps/frontend/src/lib/authClient.ts"], { cwd: repository });
    execFileSync("git", [
      "-c", "user.name=Contract Test",
      "-c", "user.email=contract@example.invalid",
      "commit", "--quiet", "-m", "add frontend clients",
    ], { cwd: repository });

    const sources = loadFrontendSourcesFromGitRef("HEAD", repository, [
      "apps/frontend/src/lib/api.ts",
      "apps/frontend/src/lib/authClient.ts",
    ]);
    assert.deepEqual(sources.map(source => source.text), [
      "fetch(`${API_BASE_URL}/machines`);",
      "postAuthSession(\"login\", {});",
    ]);
  } finally {
    rmSync(repository, { recursive: true, force: true });
  }
});

test("loads current frontend production sources recursively with normalized paths", () => {
  const repository = mkdtempSync(join(tmpdir(), "frontend-client-working-tree-"));
  try {
    const apiPath = join(repository, "apps", "frontend", "src", "lib", "api.ts");
    const pagePath = join(repository, "apps", "frontend", "src", "pages", "Machines.tsx");
    mkdirSync(join(repository, "apps", "frontend", "src", "lib"), { recursive: true });
    mkdirSync(join(repository, "apps", "frontend", "src", "pages"), { recursive: true });
    writeFileSync(apiPath, "export async function getMachines() { return authFetch(`${API_BASE_URL}/machines`); }");
    writeFileSync(pagePath, "import { getMachines } from \"@/lib/api\"; getMachines();");

    const sources = loadFrontendSourcesFromWorkingTree(repository);
    assert.deepEqual(sources.map(source => source.filename), [
      "apps/frontend/src/lib/api.ts",
      "apps/frontend/src/pages/Machines.tsx",
    ]);
    assert.equal(sources[0].text, readFileSync(apiPath, "utf8"));
  } finally {
    rmSync(repository, { recursive: true, force: true });
  }
});

test("ignores routes declared only by unreferenced exported client helpers", () => {
  const directory = mkdtempSync(join(tmpdir(), "frontend-api-dead-helper-"));
  const apiSource = {
    filename: "apps/frontend/src/lib/api.ts",
    text: [
      'export async function getMachines() { return authFetch(`${API_BASE_URL}/machines`); }',
      'export async function saveDeprecatedIntegration() {',
      '  return authFetch(`${API_BASE_URL}/payments/integration`, { method: "POST" });',
      '}',
    ].join("\n"),
  };
  const pageSource = {
    filename: "apps/frontend/src/pages/Machines.tsx",
    text: `import { getMachines } from "@/lib/api"; getMachines();`,
  };
  try {
    writeFileSync(join(directory, "MachinesController.cs"), `
      [Route("api/[controller]")]
      public class MachinesController : ControllerBase
      {
        [HttpGet] public IActionResult GetMachines() => Ok();
      }
    `);
    const usedFunctions = referencedClientFunctions([apiSource], [apiSource, pageSource]);
    assert.deepEqual([...usedFunctions], ["getMachines"]);
    assert.deepEqual(findContractMismatches([apiSource], directory, usedFunctions), []);
  } finally {
    rmSync(join(directory, "MachinesController.cs"), { force: true });
    rmdirSync(directory);
  }
});

test("delta contract check tolerates existing gaps but rejects newly introduced missing routes", () => {
  const directory = mkdtempSync(join(tmpdir(), "frontend-api-contract-delta-"));
  try {
    writeFileSync(join(directory, "ProductsController.cs"), `
      [Route("api/[controller]")]
      public class ProductsController : ControllerBase
      {
        [HttpGet] public IActionResult Get() => Ok();
      }
    `);
    const baseSources = [
      { filename: "apps/frontend/src/lib/api.ts", text: 'export function listProducts() { return authFetch(`${API_BASE_URL}/products`); }' },
      { filename: "apps/frontend/src/pages/Products.tsx", text: 'import { listProducts } from "@/lib/api"; listProducts(); authFetch(`${API_BASE_URL}/products/types/${"id"}`, { method: "DELETE" });' },
    ];
    const existingGap = findNewContractMismatches(baseSources, baseSources, directory);
    assert.deepEqual(existingGap, []);

    const changedSources = baseSources.map(source => ({ ...source }));
    changedSources[1] = {
      ...changedSources[1],
      text: `${changedSources[1].text}\nauthFetch(\`${"${API_BASE_URL}"}/products/${"${id}"}\`, { method: "DELETE" });`,
    };
    assert.deepEqual(findNewContractMismatches(changedSources, baseSources, directory), ["DELETE /products/{}"]);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});
