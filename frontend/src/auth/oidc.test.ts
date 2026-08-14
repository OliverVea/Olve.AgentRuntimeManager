import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import * as auth from "./oidc.js";

const AUTH_CONFIG = {
  authority: "https://auth.example/application/o/spa/",
  clientId: "spa",
  scopes: "openid profile offline_access",
};
const DISCOVERY = {
  authorization_endpoint: "https://auth.example/authorize",
  token_endpoint: "https://auth.example/token",
  end_session_endpoint: "https://auth.example/logout",
};

const b64url = (o: unknown) =>
  btoa(JSON.stringify(o)).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
const idToken = (claims: Record<string, unknown>) =>
  `${b64url({ alg: "none" })}.${b64url(claims)}.sig`;

/** Route fetch by URL: auth-config, discovery, and a scriptable token endpoint. */
function mockFetch(tokenResponses: Array<{ status?: number; body?: unknown }>) {
  let tokenCall = 0;
  const fn = vi.fn(async (url: string | URL) => {
    const u = String(url);
    if (u.endsWith("/api/auth-config")) return Response.json(AUTH_CONFIG);
    if (u.endsWith("/.well-known/openid-configuration")) return Response.json(DISCOVERY);
    if (u === DISCOVERY.token_endpoint) {
      const r = tokenResponses[Math.min(tokenCall++, tokenResponses.length - 1)];
      return new Response(r.body === undefined ? "{}" : JSON.stringify(r.body), {
        status: r.status ?? 200,
        headers: { "Content-Type": "application/json" },
      });
    }
    throw new Error(`unexpected fetch: ${u}`);
  });
  vi.stubGlobal("fetch", fn);
  return fn;
}

beforeEach(() => {
  localStorage.clear();
  sessionStorage.clear();
  vi.spyOn(window.location, "assign").mockImplementation(() => {});
});

afterEach(() => {
  auth.logout(); // resets module singleton state (tokens, timer)
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe("oidc", () => {
  it("starts logged out", async () => {
    mockFetch([]);
    await auth.init();
    expect(auth.isAuthenticated()).toBe(false);
    expect(await auth.getAccessToken()).toBeNull();
  });

  it("restores a session from a persisted refresh token and exposes the user", async () => {
    localStorage.setItem("olve-arm.oidc.refresh", "rt-1");
    mockFetch([
      {
        body: {
          access_token: "at-1",
          refresh_token: "rt-2",
          expires_in: 3600,
          id_token: idToken({ sub: "u1", name: "Ada Lovelace", email: "ada@example.com" }),
        },
      },
    ]);

    await auth.init();

    expect(auth.isAuthenticated()).toBe(true);
    expect(await auth.getAccessToken()).toBe("at-1");
    expect(auth.getUser()?.name).toBe("Ada Lovelace");
  });

  it("proactively refreshes an access token inside the expiry skew window", async () => {
    localStorage.setItem("olve-arm.oidc.refresh", "rt-1");
    mockFetch([
      { body: { access_token: "at-old", refresh_token: "rt-2", expires_in: 10 } }, // within 60s skew
      { body: { access_token: "at-new", refresh_token: "rt-3", expires_in: 3600 } },
    ]);

    await auth.init(); // first refresh → at-old, but it's already inside the skew window
    const token = await auth.getAccessToken(); // should trigger a second refresh

    expect(token).toBe("at-new");
  });

  it("clears the session when the refresh token is rejected", async () => {
    localStorage.setItem("olve-arm.oidc.refresh", "rt-expired");
    mockFetch([{ status: 400, body: { error: "invalid_grant" } }]);

    await auth.init();

    expect(auth.isAuthenticated()).toBe(false);
    expect(localStorage.getItem("olve-arm.oidc.refresh")).toBeNull();
  });

  it("login() stashes a PKCE verifier and redirects to the authorize endpoint with S256", async () => {
    mockFetch([]);
    await auth.init();

    await auth.login();

    const stash = JSON.parse(sessionStorage.getItem("olve-arm.oidc.pkce") ?? "{}");
    expect(stash.verifier).toBeTruthy();
    expect(stash.state).toBeTruthy();

    const target = vi.mocked(window.location.assign).mock.calls[0][0] as string;
    expect(target).toContain(`${DISCOVERY.authorization_endpoint}?`);
    expect(target).toContain("code_challenge_method=S256");
    expect(target).toContain("client_id=spa");
    expect(target).not.toContain(stash.verifier); // only the challenge (hash) goes on the wire
  });
});
