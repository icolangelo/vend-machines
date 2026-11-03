#!/usr/bin/env node

import { mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { basename, dirname, join, relative, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { execFileSync } from "node:child_process";
import { createRequire } from "node:module";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = join(scriptDirectory, "..", "..");
const require = createRequire(join(repositoryRoot, "apps", "frontend", "package.json"));
const ts = require("typescript");

const frontendFiles = [
  join(repositoryRoot, "apps", "frontend", "src", "lib", "api.ts"),
  join(repositoryRoot, "apps", "frontend", "src", "lib", "authClient.ts"),
];
const controllerDirectory = join(repositoryRoot, "apps", "backend", "VendingMachines.Api", "Controllers");

function canonicalPath(value) {
  let path = value.split("?", 1)[0].replace(/\$\{query\}$/i, "");
  path = path.replace(/\{[^}:]+(?::[^}]+)?\}/g, "{}");
  path = path.replace(/\[controller\]/gi, "{}");
  path = path.replace(/\/+$/, "").toLowerCase();
  if (!path.startsWith("/")) path = `/${path}`;
  if (path.startsWith("/api/")) path = path.slice(4);
  else if (path === "/api") path = "/";
  return path || "/";
}

function templatePath(template, sourceFile) {
  let path = template.head.text;
  for (const span of template.templateSpans) {
    const expression = span.expression;
    if (ts.isIdentifier(expression) && expression.text === "path") return null;
    if (ts.isIdentifier(expression) && expression.text === "API_BASE_URL") {
      // The configured API base already ends in /api and is omitted for comparison.
    } else if (ts.isIdentifier(expression) && expression.text === "query") {
      // URLSearchParams is appended as a query string to the preceding route.
    } else {
      path += "{}";
    }
    path += span.literal.text;
  }
  if (path.includes("${path}")) return null;
  return canonicalPath(path);
}

function methodFromOptions(options, sourceFile) {
  if (!options || !ts.isObjectLiteralExpression(options)) return "GET";
  const method = options.properties.find(property =>
    ts.isPropertyAssignment(property) &&
    (property.name.getText(sourceFile).replaceAll('"', "").replaceAll("'", "") === "method"));
  if (!method || !ts.isPropertyAssignment(method) || !ts.isStringLiteral(method.initializer)) return "GET";
  return method.initializer.text.toUpperCase();
}

export function parseFrontendRequestEntries(sourceText, filename = "frontend.ts") {
  const sourceFile = ts.createSourceFile(filename, sourceText, ts.ScriptTarget.Latest, true, ts.ScriptKind.TS);
  const requests = new Set();
  const entries = [];
  function ownerFunction(node) {
    let current = node.parent;
    let exportedOwner = null;
    while (current && current !== sourceFile) {
      if (ts.isFunctionDeclaration(current) && current.name && current.parent === sourceFile &&
        current.modifiers?.some(modifier => modifier.kind === ts.SyntaxKind.ExportKeyword)) {
        exportedOwner = current.name.text;
      }
      if (ts.isVariableDeclaration(current) && current.initializer && current.parent?.parent?.parent === sourceFile &&
        current.parent.parent.modifiers?.some(modifier => modifier.kind === ts.SyntaxKind.ExportKeyword)) {
        exportedOwner = current.name.getText(sourceFile).replaceAll('"', "").replaceAll("'", "");
      }
      current = current.parent;
    }
    return exportedOwner;
  }
  function addRequest(request, node) {
    requests.add(request);
    entries.push({ request, owner: ownerFunction(node) });
  }
  function visit(node) {
    if (ts.isCallExpression(node) && ts.isIdentifier(node.expression)) {
      const callName = node.expression.text;
      if (callName === "postAuthSession" && node.arguments.length > 0 && ts.isStringLiteral(node.arguments[0])) {
        addRequest(`POST /auth/${node.arguments[0].text}`, node);
      }
      if (["fetch", "authFetch", "sendInventoryMovement"].includes(callName) && node.arguments.length > 0) {
        const argument = node.arguments[0];
        if (ts.isTemplateExpression(argument)) {
          const raw = templatePath(argument, sourceFile);
          if (raw !== null) {
            const method = callName === "sendInventoryMovement" ? "POST" : methodFromOptions(node.arguments[1], sourceFile);
            addRequest(`${method} ${raw}`, node);
          }
        } else if (ts.isStringLiteral(argument) && argument.text.startsWith("${API_BASE_URL}")) {
          addRequest(`${methodFromOptions(node.arguments[1], sourceFile)} ${canonicalPath(argument.text.replace("${API_BASE_URL}", ""))}`, node);
        }
      }
    }
    ts.forEachChild(node, visit);
  }
  visit(sourceFile);
  return { requests, entries };
}

export function referencedClientFunctions(clientSources, allFrontendSources) {
  const exportedNames = new Set();
  for (const source of clientSources) {
    const sourceFile = ts.createSourceFile(source.filename, source.text, ts.ScriptTarget.Latest, true, ts.ScriptKind.TS);
    for (const statement of sourceFile.statements) {
      if (!statement.modifiers?.some(modifier => modifier.kind === ts.SyntaxKind.ExportKeyword)) continue;
      if (ts.isFunctionDeclaration(statement) && statement.name) exportedNames.add(statement.name.text);
      if (ts.isVariableStatement(statement)) {
        for (const declaration of statement.declarationList.declarations) {
          if (ts.isIdentifier(declaration.name)) exportedNames.add(declaration.name.text);
        }
      }
    }
  }
  const productionSources = allFrontendSources
    .filter(source => !source.filename.replaceAll("\\", "/").includes("/test/"))
    .filter(source => !clientSources.some(client => client.filename === source.filename));
  return new Set([...exportedNames].filter(name => {
    const reference = new RegExp(`\\b${name.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}\\b`);
    return productionSources.some(source => reference.test(source.text));
  }));
}

export function parseFrontendRequests(sourceText, filename = "frontend.ts") {
  return new Set(parseFrontendRequestEntries(sourceText, filename).entries.map(entry => entry.request));
}

function parseControllerRoutes(directory) {
  const routes = new Set();
  for (const filename of readdirSync(directory).filter(name => name.endsWith("Controller.cs"))) {
    const source = readFileSync(join(directory, filename), "utf8");
    const controller = filename.replace(/Controller\.cs$/i, "");
    const classRoute = source.match(/^\s*\[Route\("([^"]+)"\)\]/m)?.[1]
      ?.replace(/\[controller\]/gi, controller);
    if (!classRoute) continue;
    const attributes = /\[Http(Get|Post|Put|Patch|Delete)(?:\("([^"]*)"\))?\]/g;
    for (const match of source.matchAll(attributes)) {
      const method = match[1].toUpperCase();
      const actionRoute = match[2] ?? "";
      const path = actionRoute.startsWith("/") || actionRoute.startsWith("~/")
        ? actionRoute.replace(/^~/, "")
        : `${classRoute.replace(/\/$/, "")}/${actionRoute.replace(/^\//, "")}`;
      routes.add(`${method} ${canonicalPath(path)}`);
    }
  }
  return routes;
}

export function findContractMismatches(frontendSources, controllerDirectoryPath, usedClientFunctions = null) {
  const requests = new Set();
  for (const source of frontendSources) {
    for (const entry of parseFrontendRequestEntries(source.text, source.filename).entries) {
      if (entry.owner && usedClientFunctions && !usedClientFunctions.has(entry.owner)) continue;
      requests.add(entry.request);
    }
  }
  const routes = parseControllerRoutes(controllerDirectoryPath);
  return [...requests].filter(request => !routes.has(request)).sort();
}

export function materializeControllersFromGitRef(
  ref,
  repoRoot = repositoryRoot,
  controllerPath = relative(repositoryRoot, controllerDirectory).split(sep).join("/"),
) {
  const filenames = execFileSync("git", ["ls-tree", "-r", "--name-only", ref, controllerPath], {
    cwd: repoRoot,
    encoding: "utf8",
  }).split(/\r?\n/).filter(filename => filename.endsWith("Controller.cs"));
  if (filenames.length === 0) throw new Error(`Nenhum controller encontrado em ${ref}:${controllerPath}.`);

  const temporaryDirectory = mkdtempSync(join(tmpdir(), "frontend-api-contract-ref-"));
  try {
    for (const filename of filenames) {
      const source = execFileSync("git", ["show", `${ref}:${filename}`], {
        cwd: repoRoot,
        encoding: "utf8",
      });
      writeFileSync(join(temporaryDirectory, basename(filename)), source, "utf8");
    }
    return temporaryDirectory;
  } catch (error) {
    rmSync(temporaryDirectory, { recursive: true, force: true });
    throw error;
  }
}

export function loadFrontendSourcesFromGitRef(
  ref,
  repoRoot = repositoryRoot,
  sourcePaths = null,
) {
  const paths = sourcePaths ?? execFileSync("git", ["ls-tree", "-r", "--name-only", ref, "apps/frontend/src"], {
    cwd: repoRoot,
    encoding: "utf8",
  }).split(/\r?\n/).filter(filename => /\.tsx?$/.test(filename));
  if (paths.length === 0) throw new Error(`Nenhum arquivo TypeScript de frontend encontrado em ${ref}.`);
  return paths.map(filename => ({
    filename,
    text: execFileSync("git", ["show", `${ref}:${filename}`], {
      cwd: repoRoot,
      encoding: "utf8",
    }),
  }));
}

export function loadFrontendSourcesFromWorkingTree(repoRoot = repositoryRoot) {
  const sourceRoot = join(repoRoot, "apps", "frontend", "src");
  const filenames = [];
  function collect(directory) {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const filename = join(directory, entry.name);
      if (entry.isDirectory()) collect(filename);
      else if (entry.isFile() && /\.tsx?$/.test(entry.name)) filenames.push(filename);
    }
  }

  collect(sourceRoot);
  return filenames
    .sort()
    .map(filename => ({
      filename: relative(repoRoot, filename).split(sep).join("/"),
      text: readFileSync(filename, "utf8"),
    }));
}

function main() {
  const args = process.argv.slice(2);
  let backendRef = null;
  let frontendRef = null;
  if (args.length > 0) {
    if (args.length % 2 !== 0) {
      console.error("Uso: node CheckFrontendApiContract.mjs [--backend-ref <ref-git>] [--frontend-ref <ref-git>]");
      process.exitCode = 2;
      return;
    }
    for (let index = 0; index < args.length; index += 2) {
      const [option, ref] = args.slice(index, index + 2);
      if (!ref || !["--backend-ref", "--frontend-ref"].includes(option)) {
        console.error("Uso: node CheckFrontendApiContract.mjs [--backend-ref <ref-git>] [--frontend-ref <ref-git>]");
        process.exitCode = 2;
        return;
      }
      if (option === "--backend-ref") backendRef = ref;
      else frontendRef = ref;
    }
  }

  let frontendSources;
  let selectedControllerDirectory = controllerDirectory;
  try {
    let allFrontendSources;
    if (frontendRef) {
      allFrontendSources = loadFrontendSourcesFromGitRef(frontendRef);
      frontendSources = allFrontendSources.filter(source =>
        frontendFiles.some(filename => relative(repositoryRoot, filename).split(sep).join("/") === source.filename));
      if (frontendSources.length !== frontendFiles.length) {
        throw new Error(`A referência ${frontendRef} não contém todos os clientes de API esperados.`);
      }
    } else {
      frontendSources = frontendFiles.map(filename => ({
        filename: relative(repositoryRoot, filename).split(sep).join("/"),
        text: readFileSync(filename, "utf8"),
      }));
      allFrontendSources = loadFrontendSourcesFromWorkingTree();
    }
    const usedClientFunctions = referencedClientFunctions(frontendSources, allFrontendSources);
    if (backendRef) selectedControllerDirectory = materializeControllersFromGitRef(backendRef);
    const mismatches = findContractMismatches(frontendSources, selectedControllerDirectory, usedClientFunctions);
    if (mismatches.length > 0) {
      const target = backendRef ? `API em ${backendRef}` : "API atual";
      const source = frontendRef ? `frontend em ${frontendRef}` : "frontend atual";
      console.error(`Contrato ${source}/API incompatível com a ${target}: ${mismatches.length} rota(s) sem método correspondente:`);
      for (const mismatch of mismatches) console.error(`- ${mismatch}`);
      process.exitCode = 1;
      return;
    }
    const target = backendRef ? `API em ${backendRef}` : "controllers atuais";
    const source = frontendRef ? `frontend em ${frontendRef}` : "frontend atual";
    console.log(`Contrato ${source}/API verificado: todos os métodos e rotas usados pelo cliente existem nos ${target}.`);
  } catch (error) {
    const message = error instanceof Error ? error.message : "erro desconhecido";
    console.error(`Não foi possível carregar a referência selecionada: ${message}`);
    process.exitCode = 2;
  } finally {
    if (backendRef && selectedControllerDirectory !== controllerDirectory)
      rmSync(selectedControllerDirectory, { recursive: true, force: true });
  }
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) main();
