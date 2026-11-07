const developmentApiUrl = import.meta.env.DEV ? "http://localhost:5118/api" : "";
const rawApiUrl = import.meta.env.VITE_API_URL?.trim() || developmentApiUrl;

if (!rawApiUrl) {
  throw new Error("VITE_API_URL precisa estar configurada neste build.");
}

export const API_BASE_URL = rawApiUrl.endsWith("/") ? rawApiUrl.slice(0, -1) : rawApiUrl;

const REFRESH_EARLY_MS = 60_000;
const REFRESH_CONFLICT_RETRIES = 3;
const AUTH_SESSION_SYNC_CHANNEL = "vend-machines-auth-session";
const AUTH_SESSION_SYNC_STORAGE_KEY = "vend-machines-auth-session-event";

export interface AuthUser {
  id: string;
  name: string;
  email: string;
  role: string;
  companyId?: string | null;
  companyName?: string | null;
}

export interface AuthSession {
  token: string;
  expiresAt: string;
  user: AuthUser;
}

export type RegistrationResult =
  | { kind: "session"; session: AuthSession }
  | { kind: "pending"; status?: string; message: string };

export type AuthStateReason = "updated" | "expired" | "logout";
type AuthStateListener = (session: AuthSession | null, reason: AuthStateReason) => void;

let currentSession: AuthSession | null = null;
let sessionGeneration = 0;
let refreshPromise: Promise<AuthSession | null> | null = null;
let refreshTimer: ReturnType<typeof setTimeout> | null = null;
const listeners = new Set<AuthStateListener>();
let authSessionChannel: BroadcastChannel | null = null;
let storageSyncListenerAttached = false;

export class SessionExpiredError extends Error {
  constructor() {
    super("Sua sessão expirou. Entre novamente.");
    this.name = "SessionExpiredError";
  }
}

export class SessionChangedError extends Error {
  constructor() {
    super("A sessão mudou durante a operação. Confira o resultado antes de tentar novamente.");
    this.name = "SessionChangedError";
  }
}

export function subscribeToAuthState(listener: AuthStateListener): () => void {
  ensureCrossTabSessionSync();
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

export function getCurrentAuthUser(): AuthUser | null {
  return currentSession?.user ?? null;
}

export function setAuthSession(session: AuthSession): void {
  sessionGeneration += 1;
  applySession(session, "updated");
  publishCrossTabSession({ type: "session", session });
}

export function clearAuthSession(reason: AuthStateReason = "logout"): void {
  sessionGeneration += 1;
  applySession(null, reason);
  clearLegacyAuthStorage();
  publishCrossTabSession({ type: "logout" });
}

export function clearLegacyAuthStorage(): void {
  for (const storage of [sessionStorage, localStorage]) {
    storage.removeItem("token");
    storage.removeItem("user");
    storage.removeItem("isAuthenticated");
  }
}

export async function restoreSession(): Promise<AuthSession | null> {
  clearLegacyAuthStorage();
  return refreshAccessToken(false);
}

export async function loginSession(email: string, password: string): Promise<AuthSession> {
  return postAuthSession("login", { email, password });
}

export async function registerSession(payload: {
  name: string;
  email: string;
  cpf: string;
  password: string;
  companyName: string;
  companyCnpj: string;
  acceptedPrivacyPolicy: boolean;
}): Promise<RegistrationResult> {
  const response = await fetch(`${API_BASE_URL}/auth/register`, {
    method: "POST",
    credentials: "include",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(payload),
  });

  if (!response.ok) {
    const error = await readError(response, "Não foi possível enviar o cadastro.");
    throw new Error(error);
  }

  const result: unknown = await response.json();
  if (isAuthSession(result)) {
    setAuthSession(result);
    return { kind: "session", session: result };
  }

  if (result && typeof result === "object") {
    const pending = result as { status?: unknown; message?: unknown };
    if (typeof pending.message === "string" && pending.message.trim()) {
      return {
        kind: "pending",
        ...(typeof pending.status === "string" ? { status: pending.status } : {}),
        message: pending.message,
      };
    }
  }

  throw new Error("A API recebeu o cadastro, mas retornou uma resposta inesperada. Tente entrar ou contate o suporte.");
}

export async function acceptInvitationSession(payload: {
  invitationToken: string;
  name: string;
  cpf: string;
  password: string;
  acceptedPrivacyPolicy: boolean;
}): Promise<AuthSession> {
  return postAuthSession("accept-invitation", payload);
}

export async function logoutSession(): Promise<void> {
  const logoutGeneration = sessionGeneration;
  try {
    await fetch(`${API_BASE_URL}/auth/logout`, {
      method: "POST",
      credentials: "include",
    });
  } finally {
    if (logoutGeneration === sessionGeneration) clearAuthSession("logout");
  }
}

export async function authenticatedFetch(url: string, options: RequestInit = {}): Promise<Response> {
  const requestGeneration = sessionGeneration;
  const requestUserId = currentSession?.user.id;
  const impersonatedCompanyId = readImpersonatedCompanyId();
  const session = await ensureFreshSession();
  if (requestGeneration !== sessionGeneration) throw new SessionChangedError();
  if (requestUserId && session && requestUserId !== session.user.id) throw new SessionChangedError();
  if (!session) {
    throw new SessionExpiredError();
  }

  const tokenUsed = session?.token ?? null;
  const response = await sendAuthenticatedRequest(url, options, tokenUsed, impersonatedCompanyId);

  if (requestGeneration !== sessionGeneration) throw new SessionChangedError();

  if (response.status !== 401 || !tokenUsed) {
    return response;
  }

  if (currentSession?.token && currentSession.token !== tokenUsed) {
    const retry = await sendAuthenticatedRequest(url, options, currentSession.token, impersonatedCompanyId);
    if (requestGeneration !== sessionGeneration) throw new SessionChangedError();
    return retry;
  }
  if (currentSession?.token === tokenUsed) {
    clearRefreshTimer();
  }

  const renewed = await refreshAccessToken(true);
  if (requestGeneration !== sessionGeneration) throw new SessionChangedError();
  if (requestUserId && renewed && requestUserId !== renewed.user.id) throw new SessionChangedError();
  if (!renewed) {
    throw new SessionExpiredError();
  }

  const retry = await sendAuthenticatedRequest(url, options, renewed.token, impersonatedCompanyId);
  if (requestGeneration !== sessionGeneration) throw new SessionChangedError();
  return retry;
}

async function postAuthSession(path: string, body: unknown): Promise<AuthSession> {
  const response = await fetch(`${API_BASE_URL}/auth/${path}`, {
    method: "POST",
    credentials: "include",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });

  if (!response.ok) {
    const error = await readError(response, "Não foi possível autenticar.");
    throw new Error(error);
  }

  const session = (await response.json()) as AuthSession;
  sessionGeneration += 1;
  applySession(session, "updated");
  publishCrossTabSession({ type: "session", session });
  return session;
}

async function ensureFreshSession(): Promise<AuthSession | null> {
  if (currentSession && new Date(currentSession.expiresAt).getTime() - Date.now() > REFRESH_EARLY_MS) {
    return currentSession;
  }

  return refreshAccessToken(true);
}

async function refreshAccessToken(notifyExpiration: boolean): Promise<AuthSession | null> {
  if (!refreshPromise) {
    refreshPromise = performRefresh(notifyExpiration).finally(() => {
      refreshPromise = null;
    });
  }

  return refreshPromise;
}

async function performRefresh(notifyExpiration: boolean): Promise<AuthSession | null> {
  const refreshGeneration = sessionGeneration;
  const refreshUserId = currentSession?.user.id;
  for (let attempt = 0; attempt <= REFRESH_CONFLICT_RETRIES; attempt += 1) {
    let response: Response;
    try {
      response = await fetch(`${API_BASE_URL}/auth/refresh`, {
        method: "POST",
        credentials: "include",
      });
    } catch (error) {
      if (refreshGeneration !== sessionGeneration) return null;
      if (notifyExpiration) {
        throw error;
      }
      return null;
    }

    if (refreshGeneration !== sessionGeneration) return null;
    if (response.ok) {
      const session = (await response.json()) as AuthSession;
      if (refreshGeneration !== sessionGeneration) return null;
      if (refreshUserId && refreshUserId !== session.user.id) throw new SessionChangedError();
      applySession(session, "updated");
      return session;
    }

    if (response.status === 409 && attempt < REFRESH_CONFLICT_RETRIES) {
      await delay(150 * (attempt + 1));
      continue;
    }

    if (response.status === 401) {
      applySession(null, notifyExpiration ? "expired" : "logout");
      publishCrossTabSession({ type: "logout" });
      return null;
    }

    const error = await readError(response, "Não foi possível renovar a sessão.");
    throw new Error(error);
  }

  return null;
}

function sendAuthenticatedRequest(
  url: string,
  options: RequestInit,
  token: string | null,
  impersonatedCompanyId: string | null,
): Promise<Response> {
  const headers = new Headers(options.headers || {});
  if (token) {
    headers.set("Authorization", `Bearer ${token}`);
  }

  if (impersonatedCompanyId) {
    headers.set("X-Impersonate-Company-Id", impersonatedCompanyId);
  }

  return fetch(url, {
    ...options,
    credentials: "include",
    headers,
  });
}

function applySession(session: AuthSession | null, reason: AuthStateReason): void {
  currentSession = session;
  scheduleRefresh(session);
  listeners.forEach((listener) => listener(session, reason));
}

function ensureCrossTabSessionSync(): void {
  if (typeof window === "undefined") return;

  if (!storageSyncListenerAttached) {
    window.addEventListener("storage", event => {
      if (event.key !== AUTH_SESSION_SYNC_STORAGE_KEY || !event.newValue) return;
      applyExternalSessionChange({ type: "invalidate" });
    });
    storageSyncListenerAttached = true;
  }

  if (!authSessionChannel && typeof BroadcastChannel !== "undefined") {
    try {
      authSessionChannel = new BroadcastChannel(AUTH_SESSION_SYNC_CHANNEL);
      authSessionChannel.addEventListener("message", event => applyExternalSessionChange(event.data));
      (authSessionChannel as BroadcastChannel & { unref?: () => void }).unref?.();
    } catch {
      authSessionChannel = null;
    }
  }
}

function publishCrossTabSession(message: { type: "session"; session: AuthSession } | { type: "logout" }): void {
  ensureCrossTabSessionSync();
  if (authSessionChannel) {
    try {
      authSessionChannel.postMessage(message);
      return;
    } catch {
      // Fall back to invalidating other tabs if the browser cannot clone the session payload.
    }
  }

  try {
    localStorage.setItem(AUTH_SESSION_SYNC_STORAGE_KEY, JSON.stringify({ type: "invalidate", nonce: `${Date.now()}-${Math.random()}` }));
    localStorage.removeItem(AUTH_SESSION_SYNC_STORAGE_KEY);
  } catch {
    // Cross-tab synchronization is best effort when browser storage is disabled.
  }
}

function applyExternalSessionChange(value: unknown): void {
  if (!value || typeof value !== "object") return;
  const message = value as { type?: unknown; session?: unknown };
  if (message.type === "session" && isAuthSession(message.session)) {
    sessionGeneration += 1;
    applySession(message.session, "updated");
    return;
  }

  if (message.type === "logout" || message.type === "invalidate") {
    sessionGeneration += 1;
    clearLegacyAuthStorage();
    applySession(null, "logout");
  }
}

function isAuthSession(value: unknown): value is AuthSession {
  if (!value || typeof value !== "object") return false;
  const session = value as Partial<AuthSession>;
  const user = session.user as Partial<AuthUser> | null | undefined;
  return typeof session.token === "string" && session.token.length > 0 &&
    typeof session.expiresAt === "string" && Date.parse(session.expiresAt) > Date.now() &&
    !!user && typeof user.id === "string" && typeof user.name === "string" &&
    typeof user.email === "string" && typeof user.role === "string";
}

function scheduleRefresh(session: AuthSession | null): void {
  clearRefreshTimer();
  if (!session || typeof window === "undefined") {
    return;
  }

  const delayMs = Math.max(0, new Date(session.expiresAt).getTime() - Date.now() - REFRESH_EARLY_MS);
  refreshTimer = setTimeout(() => {
    void refreshAccessToken(true).catch(() => scheduleRefreshRetry());
  }, delayMs);
}

function scheduleRefreshRetry(): void {
  clearRefreshTimer();
  if (!currentSession) {
    return;
  }

  refreshTimer = setTimeout(() => {
    void refreshAccessToken(true).catch(() => scheduleRefreshRetry());
  }, 30_000);
}

function clearRefreshTimer(): void {
  if (refreshTimer) {
    clearTimeout(refreshTimer);
    refreshTimer = null;
  }
}

function readImpersonatedCompanyId(): string | null {
  try {
    const stored = sessionStorage.getItem("impersonatedCompany");
    if (!stored) return null;
    const parsed = JSON.parse(stored) as { id?: string };
    return parsed.id || null;
  } catch {
    return null;
  }
}

async function readError(response: Response, fallback: string): Promise<string> {
  const payload = await response.json().catch(() => null) as { message?: string } | null;
  return payload?.message || fallback;
}

function delay(milliseconds: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}
