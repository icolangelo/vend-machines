import { afterEach, describe, expect, it, vi } from "vitest";
import {
  authenticatedFetch,
  clearAuthSession,
  loginSession,
  setAuthSession,
  SessionExpiredError,
  subscribeToAuthState,
  type AuthSession,
} from "@/lib/authClient";

const createSession = (token: string, expiresInMs = 5 * 60_000): AuthSession => ({
  token,
  expiresAt: new Date(Date.now() + expiresInMs).toISOString(),
  user: {
    id: "user-1",
    name: "Usuário Teste",
    email: "teste@example.com",
    role: "Admin",
    companyId: "company-1",
    companyName: "Empresa Teste",
  },
});

const jsonResponse = (body: unknown, status = 200) => new Response(JSON.stringify(body), {
  status,
  headers: { "Content-Type": "application/json" },
});

describe("authClient", () => {
  afterEach(() => {
    clearAuthSession("logout");
    sessionStorage.clear();
    localStorage.clear();
    vi.restoreAllMocks();
  });

  it("mantém o access token somente em memória após o login", async () => {
    const session = createSession("login-token");
    const fetchMock = vi.spyOn(globalThis, "fetch").mockResolvedValue(jsonResponse(session));

    await loginSession("teste@example.com", "senha123");

    expect(fetchMock).toHaveBeenCalledWith(expect.stringContaining("/auth/login"), expect.objectContaining({
      credentials: "include",
      method: "POST",
    }));
    expect(sessionStorage.getItem("token")).toBeNull();
    expect(localStorage.getItem("token")).toBeNull();
  });

  it("compartilha uma única renovação entre requisições simultâneas", async () => {
    setAuthSession(createSession("expiring-token", 30_000));
    let refreshCalls = 0;
    let resourceCalls = 0;
    const renewed = createSession("renewed-token");
    vi.spyOn(globalThis, "fetch").mockImplementation(async (input) => {
      const url = String(input);
      if (url.endsWith("/auth/refresh")) {
        refreshCalls += 1;
        await Promise.resolve();
        return jsonResponse(renewed);
      }
      resourceCalls += 1;
      return jsonResponse({ ok: true });
    });

    await Promise.all([
      authenticatedFetch("https://api.example.com/resource-a"),
      authenticatedFetch("https://api.example.com/resource-b"),
    ]);

    expect(refreshCalls).toBe(1);
    expect(resourceCalls).toBe(2);
  });

  it("renova e repete uma única vez quando a API responde 401", async () => {
    setAuthSession(createSession("old-token"));
    const renewed = createSession("new-token");
    let resourceCalls = 0;
    let refreshCalls = 0;
    vi.spyOn(globalThis, "fetch").mockImplementation(async (input, init) => {
      const url = String(input);
      if (url.endsWith("/auth/refresh")) {
        refreshCalls += 1;
        return jsonResponse(renewed);
      }

      resourceCalls += 1;
      const authorization = new Headers(init?.headers).get("Authorization");
      return resourceCalls === 1
        ? jsonResponse({}, 401)
        : jsonResponse({ authorization });
    });

    const response = await authenticatedFetch("https://api.example.com/protected");
    const payload = await response.json();

    expect(refreshCalls).toBe(1);
    expect(resourceCalls).toBe(2);
    expect(payload.authorization).toBe("Bearer new-token");
  });

  it("encerra a sessão quando o refresh token não é mais válido", async () => {
    setAuthSession(createSession("old-token"));
    const reasons: string[] = [];
    const unsubscribe = subscribeToAuthState((_session, reason) => reasons.push(reason));
    vi.spyOn(globalThis, "fetch").mockImplementation(async (input) => {
      return String(input).endsWith("/auth/refresh")
        ? jsonResponse({ message: "expirada" }, 401)
        : jsonResponse({}, 401);
    });

    await expect(authenticatedFetch("https://api.example.com/protected"))
      .rejects.toBeInstanceOf(SessionExpiredError);

    expect(reasons).toContain("expired");
    unsubscribe();
  });
});
