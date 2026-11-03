const rawApiUrl = import.meta.env.VITE_API_URL || "http://localhost:5118/api";
export const API_BASE_URL = rawApiUrl.endsWith("/") ? rawApiUrl.slice(0, -1) : rawApiUrl;

const REFRESH_EARLY_MS = 60_000;
const REFRESH_CONFLICT_RETRIES = 3;

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

export type AuthStateReason = "updated" | "expired" | "logout";
type AuthStateListener = (session: AuthSession | null, reason: AuthStateReason) => void;

let currentSession: AuthSession | null = null;
let refreshPromise: Promise<AuthSession | null> | null = null;
let refreshTimer: ReturnType<typeof setTimeout> | null = null;
const listeners = new Set<AuthStateListener>();

export class SessionExpiredError extends Error {
  constructor() {
    super("Sua sessão expirou. Entre novamente.");
    this.name = "SessionExpiredError";
  }
}

export function subscribeToAuthState(listener: AuthStateListener): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

export function setAuthSession(session: AuthSession): void {
  applySession(session, "updated");
}

export function clearAuthSession(reason: AuthStateReason = "logout"): void {
  applySession(null, reason);
  clearLegacyAuthStorage();
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
}): Promise<AuthSession> {
  return postAuthSession("register", payload);
}

export async function logoutSession(): Promise<void> {
  try {
    await fetch(`${API_BASE_URL}/auth/logout`, {
      method: "POST",
      credentials: "include",
    });
  } finally {
    clearAuthSession("logout");
  }
}

export async function authenticatedFetch(url: string, options: RequestInit = {}): Promise<Response> {
  const session = await ensureFreshSession();
  if (!session) {
    throw new SessionExpiredError();
  }

  const tokenUsed = session?.token ?? null;
  const response = await sendAuthenticatedRequest(url, options, tokenUsed);

  if (response.status !== 401 || !tokenUsed) {
    return response;
  }

  if (currentSession?.token === tokenUsed) {
    clearRefreshTimer();
    currentSession = null;
  }

  const renewed = await refreshAccessToken(true);
  if (!renewed) {
    throw new SessionExpiredError();
  }

  return sendAuthenticatedRequest(url, options, renewed.token);
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
  applySession(session, "updated");
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
  for (let attempt = 0; attempt <= REFRESH_CONFLICT_RETRIES; attempt += 1) {
    let response: Response;
    try {
      response = await fetch(`${API_BASE_URL}/auth/refresh`, {
        method: "POST",
        credentials: "include",
      });
    } catch (error) {
      if (notifyExpiration) {
        throw error;
      }
      return null;
    }

    if (response.ok) {
      const session = (await response.json()) as AuthSession;
      applySession(session, "updated");
      return session;
    }

    if (response.status === 409 && attempt < REFRESH_CONFLICT_RETRIES) {
      await delay(150 * (attempt + 1));
      continue;
    }

    if (response.status === 401) {
      applySession(null, notifyExpiration ? "expired" : "logout");
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
): Promise<Response> {
  const headers = new Headers(options.headers || {});
  if (token) {
    headers.set("Authorization", `Bearer ${token}`);
  }

  const impersonated = readImpersonatedCompanyId();
  if (impersonated) {
    headers.set("X-Impersonate-Company-Id", impersonated);
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
