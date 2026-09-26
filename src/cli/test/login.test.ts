import { beforeEach, describe, expect, test } from "bun:test";
import { createHash } from "node:crypto";
import { mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { type Recorded, fakeFetch, runCli } from "./helpers";

const server = "https://arm.test";
const authority = "https://auth.test/application/o/arm-spa/";
const tokenEndpoint = "https://auth.test/application/o/token/";
const deviceEndpoint = "https://auth.test/application/o/device/";

let home = "";
beforeEach(() => {
  if (home) rmSync(home, { recursive: true, force: true });
  home = mkdtempSync(join(tmpdir(), "arm-login-"));
});

const env = (extra: Record<string, string> = {}) => ({ ARM_HOME: home, ARM_URL: server, DISPLAY: ":0", ...extra });
const readJson = (file: string) => JSON.parse(readFileSync(join(home, file), "utf8"));

/** The server's auth config, the provider's discovery document, then `onToken` for token requests. */
function provider(onToken: (req: Recorded) => { status?: number; body: unknown }, opts: { device?: boolean } = {}) {
  return fakeFetch((req) => {
    if (req.url === `${server}/api/auth-config`) {
      return { body: { authority, clientId: "arm-spa", scopes: "openid" } };
    }
    if (req.url === `${authority}.well-known/openid-configuration`) {
      return {
        body: {
          authorization_endpoint: "https://auth.test/application/o/authorize/",
          token_endpoint: tokenEndpoint,
          ...(opts.device ? { device_authorization_endpoint: deviceEndpoint } : {}),
        },
      };
    }
    if (req.url === deviceEndpoint) {
      return { body: { device_code: "dev-1", user_code: "ABCD-EFGH", verification_uri: "https://auth.test/device", interval: 1 } };
    }
    if (req.url === tokenEndpoint) return onToken(req);
    return { status: 500, body: { unexpected: req.url } };
  });
}

/** Plays the browser: follows the authorize URL's redirect back to the CLI's loopback listener. */
function browser(params: (state: string) => Record<string, string>) {
  const opened: URL[] = [];
  return {
    opened,
    open: (url: string) => {
      const authorize = new URL(url);
      opened.push(authorize);
      const redirect = new URL(authorize.searchParams.get("redirect_uri")!);
      for (const [k, v] of Object.entries(params(authorize.searchParams.get("state")!))) redirect.searchParams.set(k, v);
      void fetch(redirect);
    },
  };
}

describe("arm login (browser)", () => {
  test("PKCE code flow via the loopback; saves the token (0600) and makes the server the default", async () => {
    const f = provider(() => ({ body: { access_token: "at-1", refresh_token: "rt-1", expires_in: 300 } }));
    const b = browser((state) => ({ code: "the-code", state }));

    const r = await runCli(["login"], { env: env(), fetch: f.fetch, openBrowser: b.open, now: () => new Date("2026-09-26T10:00:00Z") });

    expect(r.code).toBe(0);
    expect(r.stdout).toContain(`Logged in to ${server}.`);
    const authorize = b.opened[0]!;
    expect(authorize.searchParams.get("client_id")).toBe("arm-spa");
    expect(authorize.searchParams.get("code_challenge_method")).toBe("S256");
    expect(authorize.searchParams.get("redirect_uri")).toMatch(/^http:\/\/127\.0\.0\.1:\d+\/callback$/);

    const exchange = new URLSearchParams(f.requests.find((q) => q.url === tokenEndpoint)!.body as string);
    expect(exchange.get("grant_type")).toBe("authorization_code");
    expect(exchange.get("code")).toBe("the-code");
    expect(exchange.get("redirect_uri")).toBe(authorize.searchParams.get("redirect_uri"));
    const verifier = exchange.get("code_verifier")!;
    expect(createHash("sha256").update(verifier).digest("base64url")).toBe(authorize.searchParams.get("code_challenge"));
    expect(readJson("credentials.json")[server]).toEqual({
      accessToken: "at-1",
      refreshToken: "rt-1",
      expiresAt: "2026-09-26T10:05:00.000Z",
      clientId: "arm-spa",
      tokenEndpoint,
    });
    expect(statSync(join(home, "credentials.json")).mode & 0o777).toBe(0o600);
    expect(readJson("config.json").url).toBe(server);
  });

  test("a denied login in the browser is an error, and nothing is saved", async () => {
    const f = provider(() => ({ body: {} }));
    const b = browser(() => ({ error: "access_denied", error_description: "User denied" }));

    const r = await runCli(["login"], { env: env(), fetch: f.fetch, openBrowser: b.open });

    expect(r.code).toBe(1);
    expect(r.stderr).toContain("authorization failed: User denied");
    expect(() => readJson("credentials.json")).toThrow();
  });

  test("a redirect with the wrong state is rejected", async () => {
    const f = provider(() => ({ body: {} }));
    const b = browser(() => ({ code: "c", state: "forged" }));

    const r = await runCli(["login"], { env: env(), fetch: f.fetch, openBrowser: b.open });

    expect(r.code).toBe(1);
    expect(r.stderr).toContain("state mismatch");
  });
});

describe("arm login (device)", () => {
  test("headless: polls through authorization_pending until approved", async () => {
    let polls = 0;
    const f = provider(
      () => (++polls < 3 ? { status: 400, body: { error: "authorization_pending" } } : { body: { access_token: "at-d", expires_in: 60 } }),
      { device: true },
    );

    const r = await runCli(["login"], { env: env({ DISPLAY: "", SSH_TTY: "/dev/pts/1" }), fetch: f.fetch });

    expect(r.code).toBe(0);
    expect(r.stderr).toContain("Code: ABCD-EFGH");
    expect(polls).toBe(3);
    expect(readJson("credentials.json")[server].accessToken).toBe("at-d");
    expect(r.stdout).toContain("no refresh token");
  });

  test("a client without the device grant gets an explanation, not the raw OAuth error", async () => {
    const f = fakeFetch((req) => {
      if (req.url === `${server}/api/auth-config`) return { body: { authority, clientId: "arm-spa", scopes: "openid" } };
      if (req.url.endsWith("openid-configuration")) {
        return { body: { authorization_endpoint: "https://auth.test/a", token_endpoint: tokenEndpoint, device_authorization_endpoint: deviceEndpoint } };
      }
      return { status: 400, body: { error: "invalid_client", error_description: "Client authentication failed" } };
    });
    const r = await runCli(["login", "--device"], { env: env(), fetch: f.fetch });
    expect(r.code).toBe(1);
    expect(r.stderr).toContain("doesn't allow device code login");
  });

  test("--device without a device endpoint is an error", async () => {
    const f = provider(() => ({ body: {} }));
    const r = await runCli(["login", "--device"], { env: env(), fetch: f.fetch });
    expect(r.code).toBe(1);
    expect(r.stderr).toContain("no device code flow");
  });
});

describe("the saved token", () => {
  const session = { items: [], total: 0, limit: 20, offset: 0 };
  const save = (credential: Record<string, unknown>) =>
    writeFileSync(join(home, "credentials.json"), JSON.stringify({ [server]: { clientId: "arm-spa", tokenEndpoint, ...credential } }));

  test("is sent as the bearer", async () => {
    save({ accessToken: "saved", expiresAt: "2026-09-26T11:00:00Z" });
    const f = fakeFetch(() => ({ body: session }));
    const r = await runCli(["session", "list"], { env: env(), fetch: f.fetch, now: () => new Date("2026-09-26T10:00:00Z") });
    expect(r.code).toBe(0);
    expect(f.requests[0]!.headers.get("authorization")).toBe("Bearer saved");
  });

  test("is refreshed when expired, and the new one saved", async () => {
    save({ accessToken: "old", refreshToken: "rt", expiresAt: "2026-09-26T09:00:00Z" });
    const f = fakeFetch((req) =>
      req.url === tokenEndpoint ? { body: { access_token: "new", expires_in: 300 } } : { body: session },
    );
    const r = await runCli(["session", "list"], { env: env(), fetch: f.fetch, now: () => new Date("2026-09-26T10:00:00Z") });
    expect(r.code).toBe(0);
    expect(f.requests.at(-1)!.headers.get("authorization")).toBe("Bearer new");
    const saved = readJson("credentials.json")[server];
    expect([saved.accessToken, saved.refreshToken]).toEqual(["new", "rt"]);
  });

  test("expired without a refresh token asks you to log in again", async () => {
    save({ accessToken: "old", expiresAt: "2026-09-26T09:00:00Z" });
    const r = await runCli(["session", "list"], { env: env(), fetch: fakeFetch(() => ({ body: session })).fetch, now: () => new Date("2026-09-26T10:00:00Z") });
    expect(r.code).toBe(1);
    expect(r.stderr).toContain("run `arm login`");
  });

  test("--token beats the saved one, and logout forgets it", async () => {
    save({ accessToken: "saved" });
    const f = fakeFetch(() => ({ body: session }));
    await runCli(["session", "list", "--token", "flag"], { env: env(), fetch: f.fetch });
    expect(f.requests[0]!.headers.get("authorization")).toBe("Bearer flag");

    const out = await runCli(["logout"], { env: env() });
    expect(out.stdout).toBe(`Logged out of ${server}.`);
    expect(readJson("credentials.json")).toEqual({});
  });
});

test("PKCE: the challenge is base64url(SHA-256(verifier))", async () => {
  const { codeChallenge } = await import("../src/auth/oidc");
  const verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
  expect(codeChallenge(verifier)).toBe(createHash("sha256").update(verifier).digest("base64url"));
  expect(codeChallenge(verifier)).toBe("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");
});
