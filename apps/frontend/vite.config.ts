import { defineConfig, loadEnv } from "vite";
import react from "@vitejs/plugin-react-swc";
import path from "path";

// https://vitejs.dev/config/
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), "");
  if (mode === "production") {
    const apiUrl = env.VITE_API_URL?.trim();
    let isPublishedHttpsApi = false;
    if (apiUrl) {
      try {
        const parsedApiUrl = new URL(apiUrl);
        const host = parsedApiUrl.hostname.toLowerCase().replace(/\.$/, "");
        const isExampleHost = host === "example" || host.endsWith(".example") ||
          ["example.com", "example.org", "example.net"]
            .some(domain => host === domain || host.endsWith(`.${domain}`));
        const isLocalHost = host === "localhost" || host.endsWith(".localhost") ||
          /^127(?:\.\d{1,3}){3}$/.test(host) || host === "0.0.0.0" || host === "::1" || host === "[::1]" ||
          host.endsWith(".invalid") || host.endsWith(".test") || host.endsWith(".local");
        const hasApiPath = parsedApiUrl.pathname.replace(/\/+$/, "").endsWith("/api");
        isPublishedHttpsApi = parsedApiUrl.protocol === "https:" && !isExampleHost && !isLocalHost &&
          !parsedApiUrl.username && !parsedApiUrl.password && !parsedApiUrl.search &&
          !parsedApiUrl.hash && hasApiPath;
      } catch {
        isPublishedHttpsApi = false;
      }
    }
    if (env.VITE_IS_TEST_ENVIRONMENT === "true" || !isPublishedHttpsApi) {
      throw new Error("Build de produção exige VITE_IS_TEST_ENVIRONMENT=false e VITE_API_URL apontando para a API publicada.");
    }
  }

  return ({
  base: '/',
  server: {
    host: "::",
    port: 8080,
    hmr: {
      overlay: false,
    },
  },
  plugins: [react()],
  resolve: {
    alias: {
      "@": path.resolve(__dirname, "./src"),
    },
    dedupe: ["react", "react-dom", "react/jsx-runtime", "react/jsx-dev-runtime"],
  },
  });
});
