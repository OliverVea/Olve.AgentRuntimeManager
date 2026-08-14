/**
 * `oidc.ts` — a small, dependency-free OpenID Connect **Authorization Code + PKCE** client for
 * the SPA, built on `fetch` + Web Crypto. It handles login, the redirect callback, silent token
 * refresh (proactive *and* on 401), and logout, behind a tiny surface the rest of the app uses:
 * {@link getAccessToken}, {@link login}, {@link logout}, {@link getUser}, {@link isAuthenticated}.
 *
 * ## Where tokens live (and the tradeoff)
 * The **access token** lives in memory only (lost on reload — re-minted from the refresh token).
 * The **refresh token** is persisted to `localStorage` so a reload doesn't force a re-login; this
 * is the standard SPA compromise and means a successful XSS could exfiltrate it. The strictly
 * safer design is a backend-for-frontend (httpOnly cookie) — see the frontend README. Storage is
 * isolated to {@link store}/{@link load} so swapping it (memory-only, BFF, …) is a local change.
 *
 * ## Config comes from the API at runtime
 * `GET /api/auth-config` returns `{ authority, clientId, scopes }` — not baked into the bundle,
 * because one image serves multiple Authentik environments (beta/prod). The authority's
 * `.well-known/openid-configuration` supplies the authorize/token/end-session endpoints.
 */

/** Shape returned by `GET /api/auth-config`. */
interface AuthConfig {
  authority: string;
  clientId: string;
  scopes: string;
}

/** The subset of OIDC discovery metadata we use. */
interface Discovery {
  authorization_endpoint: string;
  token_endpoint: string;
  end_session_endpoint?: string;
}

/** Identity claims we surface to the UI (from the `id_token`). Not an API credential. */
export interface User {
  sub: string;
  name?: string;
  email?: string;
  preferred_username?: string;
}

const CALLBACK_PATH = "/callback";
const REFRESH_KEY = "olve-arm.oidc.refresh";
const USER_KEY = "olve-arm.oidc.user";
const PKCE_KEY = "olve-arm.oidc.pkce"; // sessionStorage: survives the redirect round-trip
const REFRESH_SKEW_S = 60; // refresh this many seconds before the access token actually expires

let config: AuthConfig | null = null;
let discovery: Discovery | null = null;

let accessToken: string | null = null;
let accessExpiresAt = 0; // epoch seconds
let refreshToken: string | null = null;
let user: User | null = null;
let refreshTimer: ReturnType<typeof setTimeout> | null = null;
let inFlightRefresh: Promise<string | null> | null = null;

const listeners = new Set<() => void>();

/** Subscribe to auth-state changes (login/logout/refresh). Returns an unsubscribe fn. */
export function onChange(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

function emit(): void {
  for (const l of listeners) l();
}

// --- PKCE / crypto helpers ---------------------------------------------------

function base64UrlEncode(bytes: ArrayBuffer): string {
  const bin = String.fromCharCode(...new Uint8Array(bytes));
  return btoa(bin).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

/** A URL-safe random string (used for `code_verifier`, `state`, `nonce`). */
function randomString(bytes = 32): string {
  const buf = new Uint8Array(bytes);
  crypto.getRandomValues(buf);
  return base64UrlEncode(buf.buffer);
}

async function s256(verifier: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(verifier));
  return base64UrlEncode(digest);
}

/** Decode a JWT payload (no verification — the API verifies; the FE only reads identity claims). */
function decodeClaims(jwt: string): Record<string, unknown> {
  const payload = jwt.split(".")[1];
  const json = atob(payload.replace(/-/g, "+").replace(/_/g, "/"));
  return JSON.parse(json);
}

function redirectUri(): string {
  return `${window.location.origin}${CALLBACK_PATH}`;
}

// --- persistence (isolated so it's easy to swap) -----------------------------

function store(): void {
  if (refreshToken) localStorage.setItem(REFRESH_KEY, refreshToken);
  else localStorage.removeItem(REFRESH_KEY);
  if (user) localStorage.setItem(USER_KEY, JSON.stringify(user));
  else localStorage.removeItem(USER_KEY);
}

function load(): void {
  refreshToken = localStorage.getItem(REFRESH_KEY);
  const rawUser = localStorage.getItem(USER_KEY);
  user = rawUser ? (JSON.parse(rawUser) as User) : null;
}

function clearSession(): void {
  accessToken = null;
  accessExpiresAt = 0;
  refreshToken = null;
  user = null;
  if (refreshTimer) clearTimeout(refreshTimer);
  refreshTimer = null;
  store();
  emit();
}

// --- token endpoint calls ----------------------------------------------------

function applyTokenResponse(data: Record<string, unknown>): void {
  accessToken = (data.access_token as string) ?? null;
  const expiresIn = Number(data.expires_in ?? 0);
  accessExpiresAt = Math.floor(Date.now() / 1000) + (Number.isFinite(expiresIn) ? expiresIn : 0);
  if (typeof data.refresh_token === "string") refreshToken = data.refresh_token; // rotation-safe
  if (typeof data.id_token === "string") {
    const c = decodeClaims(data.id_token);
    user = {
      sub: String(c.sub ?? ""),
      name: c.name as string | undefined,
      email: c.email as string | undefined,
      preferred_username: c.preferred_username as string | undefined,
    };
  }
  store();
  scheduleRefresh();
  emit();
}

async function postToken(body: Record<string, string>): Promise<Record<string, unknown>> {
  if (!discovery) throw new Error("OIDC not initialized");
  const res = await fetch(discovery.token_endpoint, {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams(body).toString(),
  });
  if (!res.ok) throw new Error(`token endpoint ${res.status}`);
  return (await res.json()) as Record<string, unknown>;
}

function scheduleRefresh(): void {
  if (refreshTimer) clearTimeout(refreshTimer);
  refreshTimer = null;
  if (!refreshToken || !accessExpiresAt) return;
  const msUntil = (accessExpiresAt - REFRESH_SKEW_S) * 1000 - Date.now();
  refreshTimer = setTimeout(() => void refresh(), Math.max(msUntil, 1000));
}

/** Force a refresh_token grant. De-duplicates concurrent callers. Clears session on failure. */
export function refresh(): Promise<string | null> {
  if (inFlightRefresh) return inFlightRefresh;
  if (!refreshToken || !config) return Promise.resolve(null);
  const cfg = config;
  const rt = refreshToken;
  inFlightRefresh = (async () => {
    try {
      const data = await postToken({
        grant_type: "refresh_token",
        refresh_token: rt,
        client_id: cfg.clientId,
      });
      applyTokenResponse(data);
      return accessToken;
    } catch {
      clearSession(); // refresh token expired/revoked → back to logged-out
      return null;
    } finally {
      inFlightRefresh = null;
    }
  })();
  return inFlightRefresh;
}

// --- public API --------------------------------------------------------------

/**
 * Bootstrap: load persisted state, fetch config + discovery, complete a redirect callback if this
 * load is one, otherwise mint a fresh access token from a stored refresh token. Safe to call once
 * at startup. Returns true if the app ended up authenticated.
 */
export async function init(): Promise<boolean> {
  load();
  const cfg = (await fetch("/api/auth-config").then((r) => r.json())) as AuthConfig;
  config = cfg;
  const authority = cfg.authority.replace(/\/$/, "");
  discovery = (await fetch(`${authority}/.well-known/openid-configuration`).then((r) =>
    r.json(),
  )) as Discovery;

  if (window.location.pathname === CALLBACK_PATH && window.location.search.includes("code=")) {
    await handleCallback();
  } else if (refreshToken) {
    await refresh(); // reload with a persisted session → get a working access token
  }
  return isAuthenticated();
}

/** Start a login: build the PKCE challenge, stash the verifier, and redirect to Authentik. */
export async function login(): Promise<void> {
  if (!config || !discovery) throw new Error("OIDC not initialized");
  const cfg = config;
  const verifier = randomString();
  const state = randomString(16);
  const nonce = randomString(16);
  sessionStorage.setItem(PKCE_KEY, JSON.stringify({ verifier, state, nonce }));

  const challenge = await s256(verifier);
  const params = new URLSearchParams({
    response_type: "code",
    client_id: cfg.clientId,
    redirect_uri: redirectUri(),
    scope: cfg.scopes,
    state,
    nonce,
    code_challenge: challenge,
    code_challenge_method: "S256",
  });
  window.location.assign(`${discovery.authorization_endpoint}?${params.toString()}`);
}

/** Complete the redirect: validate `state`, exchange `code` (+ verifier), then clean the URL. */
async function handleCallback(): Promise<void> {
  const url = new URL(window.location.href);
  const code = url.searchParams.get("code");
  const returnedState = url.searchParams.get("state");
  const saved = sessionStorage.getItem(PKCE_KEY);
  sessionStorage.removeItem(PKCE_KEY);
  history.replaceState({}, "", "/"); // strip ?code from history regardless of outcome

  if (!code || !saved || !config) return;
  const cfg = config;
  const { verifier, state } = JSON.parse(saved) as { verifier: string; state: string };
  if (returnedState !== state) return; // CSRF guard: state mismatch → drop it

  const data = await postToken({
    grant_type: "authorization_code",
    code,
    redirect_uri: redirectUri(),
    client_id: cfg.clientId,
    code_verifier: verifier,
  });
  applyTokenResponse(data);
}

/**
 * Return a currently-valid access token, refreshing proactively if it's within the skew window.
 * Wired into the Kiota auth provider, so every API call rides a fresh token. Returns null when
 * logged out.
 */
export async function getAccessToken(): Promise<string | null> {
  const now = Math.floor(Date.now() / 1000);
  if (accessToken && now < accessExpiresAt - REFRESH_SKEW_S) return accessToken;
  if (refreshToken) return refresh();
  return null;
}

export function isAuthenticated(): boolean {
  return accessToken !== null || refreshToken !== null;
}

export function getUser(): User | null {
  return user;
}

/** Clear local session and, if the provider supports it, end the Authentik SSO session too. */
export function logout(): void {
  const endSession = discovery?.end_session_endpoint;
  clearSession();
  if (endSession) {
    const params = new URLSearchParams({ post_logout_redirect_uri: window.location.origin });
    window.location.assign(`${endSession}?${params.toString()}`);
  }
}
