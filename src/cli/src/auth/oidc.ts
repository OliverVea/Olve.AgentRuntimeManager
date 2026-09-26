/**
 * The OIDC pieces `arm login` needs, as in `pl login`: discovery, PKCE, the authorization-code
 * exchange, refresh and the device grant (RFC 8628). Public client only: no secret is ever sent.
 */
import { createHash, randomBytes } from "node:crypto";

export const SCOPE = "openid profile email offline_access";

export type Discovery = {
  authorizationEndpoint: string;
  tokenEndpoint: string;
  deviceAuthorizationEndpoint?: string;
};

export type TokenSet = {
  accessToken: string;
  refreshToken?: string;
  /** ISO-8601 UTC, when the access token expires (if the provider said). */
  expiresAt?: string;
};

export type DeviceStart = {
  deviceCode: string;
  userCode: string;
  verificationUri: string;
  verificationUriComplete?: string;
  interval: number;
};

/** A login step failed; the message says which and why. Exit code 1. */
export class LoginError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "LoginError";
  }
}

export const base64Url = (bytes: Buffer): string => bytes.toString("base64url");

/** A high-entropy PKCE code verifier (43 base64url characters from 32 random bytes). */
export const createCodeVerifier = (): string => base64Url(randomBytes(32));

/** The S256 code challenge of a verifier: base64url(SHA-256(verifier)). */
export const codeChallenge = (verifier: string): string =>
  base64Url(createHash("sha256").update(verifier, "ascii").digest());

export const createState = (): string => base64Url(randomBytes(16));

async function json(fetchFn: typeof fetch, url: string, init?: RequestInit): Promise<{ status: number; body: Record<string, unknown> }> {
  let response: Response;
  try {
    response = await fetchFn(url, init);
  } catch (error) {
    throw new LoginError(`request to ${url} failed: ${(error as Error).message}`);
  }
  const text = await response.text();
  let body: Record<string, unknown> = {};
  try {
    body = text ? (JSON.parse(text) as Record<string, unknown>) : {};
  } catch {
    body = { error_description: text.slice(0, 300) };
  }
  return { status: response.status, body };
}

const str = (value: unknown): string | undefined => (typeof value === "string" && value !== "" ? value : undefined);

export async function discover(fetchFn: typeof fetch, authority: string): Promise<Discovery> {
  const url = `${authority.replace(/\/+$/, "")}/.well-known/openid-configuration`;
  const { status, body } = await json(fetchFn, url);
  const authorizationEndpoint = str(body.authorization_endpoint);
  const tokenEndpoint = str(body.token_endpoint);
  if (status !== 200 || !authorizationEndpoint || !tokenEndpoint) {
    throw new LoginError(`OIDC discovery at ${url} failed (${status}): no authorization/token endpoints`);
  }
  return { authorizationEndpoint, tokenEndpoint, deviceAuthorizationEndpoint: str(body.device_authorization_endpoint) };
}

export function authorizeUrl(discovery: Discovery, clientId: string, redirectUri: string, challenge: string, state: string): string {
  const query = new URLSearchParams({
    response_type: "code",
    client_id: clientId,
    redirect_uri: redirectUri,
    scope: SCOPE,
    state,
    code_challenge: challenge,
    code_challenge_method: "S256",
  });
  return `${discovery.authorizationEndpoint}?${query}`;
}

function form(values: Record<string, string>): RequestInit {
  return { method: "POST", headers: { "Content-Type": "application/x-www-form-urlencoded" }, body: new URLSearchParams(values) };
}

function tokens(body: Record<string, unknown>, now: Date): TokenSet | undefined {
  const accessToken = str(body.access_token);
  if (!accessToken) return undefined;
  const expiresIn = typeof body.expires_in === "number" ? body.expires_in : undefined;
  return {
    accessToken,
    refreshToken: str(body.refresh_token),
    expiresAt: expiresIn !== undefined ? new Date(now.getTime() + expiresIn * 1000).toISOString() : undefined,
  };
}

async function tokenRequest(fetchFn: typeof fetch, endpoint: string, values: Record<string, string>, now: () => Date): Promise<TokenSet> {
  const { status, body } = await json(fetchFn, endpoint, form(values));
  const set = tokens(body, now());
  if (status < 200 || status >= 300 || !set) {
    const detail = str(body.error_description) ?? str(body.error) ?? "no access_token";
    throw new LoginError(`token request failed (${status}): ${detail}`);
  }
  return set;
}

export const exchangeCode = (
  fetchFn: typeof fetch, endpoint: string, clientId: string, code: string, redirectUri: string, verifier: string, now: () => Date,
): Promise<TokenSet> =>
  tokenRequest(fetchFn, endpoint, {
    grant_type: "authorization_code",
    code,
    redirect_uri: redirectUri,
    client_id: clientId,
    code_verifier: verifier,
  }, now);

export const refresh = (fetchFn: typeof fetch, endpoint: string, clientId: string, refreshToken: string, now: () => Date): Promise<TokenSet> =>
  tokenRequest(fetchFn, endpoint, { grant_type: "refresh_token", refresh_token: refreshToken, client_id: clientId }, now);

export async function startDevice(fetchFn: typeof fetch, endpoint: string, clientId: string): Promise<DeviceStart> {
  const { status, body } = await json(fetchFn, endpoint, form({ client_id: clientId, scope: SCOPE }));
  const deviceCode = str(body.device_code);
  const userCode = str(body.user_code);
  const verificationUri = str(body.verification_uri);
  if (status < 200 || status >= 300 || !deviceCode || !userCode || !verificationUri) {
    const detail = str(body.error_description) ?? str(body.error) ?? "no device_code/user_code";
    throw new LoginError(`device authorization failed (${status}): ${detail}`);
  }
  return {
    deviceCode,
    userCode,
    verificationUri,
    verificationUriComplete: str(body.verification_uri_complete),
    interval: typeof body.interval === "number" ? body.interval : 5,
  };
}

/** Polls until the user approves (or denies, or the code expires); honours `slow_down`. */
export async function pollDevice(
  fetchFn: typeof fetch,
  endpoint: string,
  clientId: string,
  device: DeviceStart,
  opts: { now: () => Date; sleep: (ms: number) => Promise<void>; signal: AbortSignal },
): Promise<TokenSet> {
  let interval = Math.max(1, device.interval);
  while (true) {
    await opts.sleep(interval * 1000);
    if (opts.signal.aborted) throw new LoginError("login cancelled");
    const { status, body } = await json(
      fetchFn,
      endpoint,
      form({ grant_type: "urn:ietf:params:oauth:grant-type:device_code", device_code: device.deviceCode, client_id: clientId }),
    );
    const set = tokens(body, opts.now());
    if (set) return set;
    switch (body.error) {
      case "authorization_pending":
        continue;
      case "slow_down":
        interval += 5;
        continue;
      case "expired_token":
        throw new LoginError("the code expired before the login was approved; run `arm login --device` again");
      case "access_denied":
        throw new LoginError("the login was denied");
      default:
        throw new LoginError(`device token poll failed (${status}): ${str(body.error_description) ?? str(body.error) ?? "no access_token"}`);
    }
  }
}
