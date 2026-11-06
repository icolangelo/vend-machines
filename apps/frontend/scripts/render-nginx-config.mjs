import { readFile, writeFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const frontendDirectory = resolve(scriptDirectory, "..");
const templatePath = resolve(frontendDirectory, "nginx.conf");
const outputPath = resolve(frontendDirectory, "nginx.conf.generated");
const apiUrlValue = process.env.VITE_API_URL;

if (!apiUrlValue) {
    throw new Error("VITE_API_URL is required to generate the frontend Content-Security-Policy.");
}

let apiUrl;
try {
    apiUrl = new URL(apiUrlValue);
} catch {
    throw new Error("VITE_API_URL must be an absolute HTTP(S) URL.");
}

if (!(["https:", "http:"].includes(apiUrl.protocol)) || apiUrl.username || apiUrl.password || apiUrl.hash) {
    throw new Error("VITE_API_URL must be an absolute HTTP(S) URL without credentials or a fragment.");
}

const isLoopback = ["localhost", "127.0.0.1", "[::1]"].includes(apiUrl.hostname);
if (apiUrl.protocol !== "https:" && !isLoopback) {
    throw new Error("VITE_API_URL must use HTTPS outside loopback development environments.");
}

const apiOrigin = apiUrl.origin;
const websocketOrigin = `${apiUrl.protocol === "https:" ? "wss:" : "ws:"}//${apiUrl.host}`;
const template = await readFile(templatePath, "utf8");
if (!template.includes("__API_ORIGIN__") || !template.includes("__WEBSOCKET_ORIGIN__")) {
    throw new Error("Nginx configuration is missing the API origin placeholders.");
}

const rendered = template
    .replaceAll("__API_ORIGIN__", apiOrigin)
    .replaceAll("__WEBSOCKET_ORIGIN__", websocketOrigin);
await writeFile(outputPath, rendered, "utf8");
