import { afterEach, describe, expect, it, vi } from "vitest";
import {
  authenticatedFetch,
  clearAuthSession,
  getCurrentAuthUser,
  loginSession,
  logoutSession,
  setAuthSession,
  SessionExpiredError,
  SessionChangedError,
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

  it.each(["company-a", null])("mantém o contexto %s enquanto aguarda a renovação inicial", async (initialCompany) => {
    setAuthSession(createSession("expiring-token", 30_000));
    if (initialCompany) sessionStorage.setItem("impersonatedCompany", JSON.stringify({ id: initialCompany }));
    let finishRefresh!: (response: Response) => void;
    let signalRefresh!: () => void;
    const refreshStarted = new Promise<void>(resolve => { signalRefresh = resolve; });
    const seenHeaders: Array<string | null> = [];
    vi.spyOn(globalThis, "fetch").mockImplementation(async (input, init) => {
      if (String(input).endsWith("/auth/refresh")) {
        signalRefresh();
        return new Promise<Response>(resolve => { finishRefresh = resolve; });
      }
      seenHeaders.push(new Headers(init?.headers).get("X-Impersonate-Company-Id"));
      return jsonResponse({ ok: true });
    });
    const request = authenticatedFetch("https://api.example.com/protected", { method: "POST" });
    await refreshStarted;
    sessionStorage.setItem("impersonatedCompany", JSON.stringify({ id: "company-b" }));
    finishRefresh(jsonResponse(createSession("new-token")));
    await request;
    expect(seenHeaders).toEqual([initialCompany]);
  });

  it("mantém a empresa original ao repetir uma requisição após resposta 401", async () => {
    setAuthSession(createSession("old-token"));
    sessionStorage.setItem("impersonatedCompany", JSON.stringify({ id: "company-a" }));
    let finishRefresh!: (response: Response) => void;
    let signalRefresh!: () => void;
    const refreshStarted = new Promise<void>(resolve => { signalRefresh = resolve; });
    const seenHeaders: Array<string | null> = [];
    vi.spyOn(globalThis, "fetch").mockImplementation(async (input, init) => {
      if (String(input).endsWith("/auth/refresh")) {
        signalRefresh();
        return new Promise<Response>(resolve => { finishRefresh = resolve; });
      }
      seenHeaders.push(new Headers(init?.headers).get("X-Impersonate-Company-Id"));
      return seenHeaders.length === 1 ? jsonResponse({}, 401) : jsonResponse({ ok: true });
    });
    const request = authenticatedFetch("https://api.example.com/protected", { method: "POST" });
    await refreshStarted;
    sessionStorage.setItem("impersonatedCompany", JSON.stringify({ id: "company-b" }));
    finishRefresh(jsonResponse(createSession("new-token")));
    await request;
    expect(seenHeaders).toEqual(["company-a", "company-a"]);
  });

  it("descarta renovação antiga quando outra conta entra durante a espera", async () => {
    setAuthSession(createSession("old-token", 30_000));
    let finishRefresh!: (response: Response) => void;
    let signalRefresh!: () => void;
    const refreshStarted = new Promise<void>(resolve => { signalRefresh = resolve; });
    const fetchMock = vi.spyOn(globalThis, "fetch").mockImplementation(async (input) => {
      if (String(input).endsWith("/auth/refresh")) {
        signalRefresh();
        return new Promise<Response>(resolve => { finishRefresh = resolve; });
      }
      return jsonResponse({ ok: true });
    });
    const request = authenticatedFetch("https://api.example.com/protected", { method: "POST" });
    const rejection = expect(request).rejects.toBeInstanceOf(SessionChangedError);
    await refreshStarted;
    const newSession = createSession("new-user-token");
    newSession.user.id = "new-user";
    setAuthSession(newSession);
    finishRefresh(jsonResponse(createSession("stale-token")));
    await rejection;
    expect(getCurrentAuthUser()?.id).toBe("new-user");
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("não repete requisição antiga com a conta nova após resposta 401 tardia", async () => {
    setAuthSession(createSession("old-token"));
    let finishRequest!: (response: Response) => void;
    let signalRequest!: () => void;
    const requestStarted = new Promise<void>(resolve => { signalRequest = resolve; });
    const fetchMock = vi.spyOn(globalThis, "fetch").mockImplementation(async () => {
      signalRequest();
      return new Promise<Response>(resolve => { finishRequest = resolve; });
    });
    const request = authenticatedFetch("https://api.example.com/protected", { method: "POST" });
    const rejection = expect(request).rejects.toBeInstanceOf(SessionChangedError);
    await requestStarted;
    const newSession = createSession("new-user-token");
    newSession.user.id = "new-user";
    setAuthSession(newSession);
    finishRequest(jsonResponse({}, 401));
    await rejection;
    expect(getCurrentAuthUser()?.id).toBe("new-user");
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("não apaga a conta nova quando o logout anterior termina atrasado", async () => {
    setAuthSession(createSession("old-token"));
    let finishLogout!: (response: Response) => void;
    vi.spyOn(globalThis, "fetch").mockImplementation(() => new Promise<Response>(resolve => { finishLogout = resolve; }));
    const logout = logoutSession();
    const newSession = createSession("new-user-token");
    newSession.user.id = "new-user";
    setAuthSession(newSession);
    finishLogout(jsonResponse({ ok: true }));
    await logout;
    expect(getCurrentAuthUser()?.id).toBe("new-user");
  });

  it("descarta respostas pendentes quando outra aba encerra a sessão", async () => {
    setAuthSession(createSession("shared-tab-token"));
    let finishRequest!: (response: Response) => void;
    let signalRequest!: () => void;
    const requestStarted = new Promise<void>(resolve => { signalRequest = resolve; });
    const fetchMock = vi.spyOn(globalThis, "fetch").mockImplementation(async () => {
      signalRequest();
      return new Promise<Response>(resolve => { finishRequest = resolve; });
    });
    const request = authenticatedFetch("https://api.example.com/protected", { method: "POST" });
    const rejection = expect(request).rejects.toBeInstanceOf(SessionChangedError);
    await requestStarted;

    window.dispatchEvent(new StorageEvent("storage", {
      key: "vend-machines-auth-session-event",
      newValue: JSON.stringify({ type: "invalidate", nonce: "other-tab-logout" }),
    }));
    finishRequest(jsonResponse({ ok: true }));

    await rejection;
    expect(getCurrentAuthUser()).toBeNull();
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("recusa renovação que retorna outra identidade sem enviar a operação", async () => {
    setAuthSession(createSession("old-token", 30_000));
    const otherSession = createSession("other-user-token");
    otherSession.user.id = "other-user";
    const fetchMock = vi.spyOn(globalThis, "fetch").mockResolvedValue(jsonResponse(otherSession));
    await expect(authenticatedFetch("https://api.example.com/protected", { method: "POST" }))
      .rejects.toBeInstanceOf(SessionChangedError);
    expect(getCurrentAuthUser()?.id).toBe("user-1");
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(String(fetchMock.mock.calls[0][0])).toContain("/auth/refresh");
  });

  it("não repete após 401 quando a renovação retorna outra identidade", async () => {
    setAuthSession(createSession("old-token"));
    const otherSession = createSession("other-user-token");
    otherSession.user.id = "other-user";
    const fetchMock = vi.spyOn(globalThis, "fetch").mockImplementation(async input =>
      String(input).endsWith("/auth/refresh") ? jsonResponse(otherSession) : jsonResponse({}, 401));
    await expect(authenticatedFetch("https://api.example.com/protected", { method: "POST" }))
      .rejects.toBeInstanceOf(SessionChangedError);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(getCurrentAuthUser()?.id).toBe("user-1");
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
