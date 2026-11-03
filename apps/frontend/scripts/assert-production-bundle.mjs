import { readdirSync, readFileSync, statSync } from "node:fs";
import { resolve, relative } from "node:path";

const distDirectory = resolve(process.argv[2] ?? "dist");
if (!statSync(distDirectory, { throwIfNoEntry: false })?.isDirectory()) {
  console.error("O diretório do bundle de produção não existe.");
  process.exit(2);
}

const forbiddenMarkers = [
  { label: "segredo OAuth simulado", value: "MOCKSECRET" },
  { label: "callback OAuth simulado", value: "dummy_mock_oauth" },
  { label: "token de sessão de teste", value: "mock-jwt-token-string" },
  { label: "chave de sessão de teste", value: "vm-test-auth-session" },
  { label: "integração Mercado Pago simulada", value: "mock_mp_integration" },
  { label: "configuração simulada", value: "mock_global_settings" },
  { label: "nome de conta de demonstração", value: "Admin Ivan (Mock)" },
  { label: "sessão MDB de fechamento de teste", value: "test-session-closure" },
  { label: "estado de evidência do alerta de teste", value: "vm-test-closure-alert-evidence" },
  { label: "alerta de fechamento simulado", value: "Encerramento físico pendente" },
];

function* javascriptFiles(directory) {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const path = resolve(directory, entry.name);
    if (entry.isDirectory()) {
      yield* javascriptFiles(path);
    } else if (entry.isFile() && /\.m?js$/i.test(entry.name)) {
      yield path;
    }
  }
}

const findings = [];
for (const file of javascriptFiles(distDirectory)) {
  const contents = readFileSync(file, "utf8");
  for (const marker of forbiddenMarkers) {
    if (contents.includes(marker.value)) {
      findings.push(`${relative(distDirectory, file)}: ${marker.label}`);
    }
  }
}

if (findings.length > 0) {
  console.error("O bundle de produção contém código ou dados exclusivos de teste:");
  for (const finding of findings) console.error(`- ${finding}`);
  process.exit(1);
}

console.log("Bundle de produção sem marcadores de mocks ou credenciais de teste.");
