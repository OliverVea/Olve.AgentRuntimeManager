import { createServer } from "node:http";
import type { AddressInfo } from "node:net";
import { authConfigGet } from "@arm/client";
import { unwrap } from "../api";
import {
  type Discovery,
  LoginError,
  type TokenSet,
  authorizeUrl,
  codeChallenge,
  createCodeVerifier,
  createState,
  discover,
  exchangeCode,
  pollDevice,
  startDevice,
} from "../auth/oidc";
import { loadConfig, saveConfig } from "../config";
import { credentialsPath, loadCredentials, saveCredentials, serverKey } from "../credentials";
import { UsageError } from "../errors";
import type { CommandContext, CommandGroup } from "../registry";

/**
 * Browser flow: authorization code + PKCE with the redirect to a loopback listener on a random
 * port (the Authentik client allows `http://127.0.0.1:<port>/callback`).
 */
async function browserFlow(ctx: CommandContext, discovery: Discovery, clientId: string): Promise<TokenSet> {
  const verifier = createCodeVerifier();
  const state = createState();

  let resolveCode!: (code: string) => void;
  let rejectCode!: (error: Error) => void;
  const code = new Promise<string>((resolve, reject) => {
    resolveCode = resolve;
    rejectCode = reject;
  });

  const server = createServer((request, response) => {
    const url = new URL(request.url ?? "/", "http://127.0.0.1");
    const page = (status: number, message: string) => {
      response.writeHead(status, { "Content-Type": "text/html; charset=utf-8" });
      response.end(
        `<!doctype html><meta charset="utf-8"><title>arm login</title>` +
          `<body style="font-family:system-ui;max-width:32rem;margin:4rem auto;text-align:center"><h2>${message}</h2>`,
      );
    };
    if (url.pathname !== "/callback") return page(404, "Not found.");
    const error = url.searchParams.get("error");
    if (error) {
      page(200, "Login failed. You can close this tab and return to the terminal.");
      return rejectCode(new LoginError(`authorization failed: ${url.searchParams.get("error_description") ?? error}`));
    }
    if (url.searchParams.get("state") !== state) {
      page(400, "State mismatch. You can close this tab.");
      return rejectCode(new LoginError("OAuth state mismatch; aborting (a stale or forged redirect)"));
    }
    const received = url.searchParams.get("code");
    if (!received) {
      page(400, "Missing authorization code. You can close this tab.");
      return rejectCode(new LoginError("the redirect carried no authorization code"));
    }
    page(200, "Logged in. You can close this tab and return to the terminal.");
    resolveCode(received);
  });

  await new Promise<void>((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
  const onAbort = () => rejectCode(new LoginError("login cancelled"));
  ctx.signal.addEventListener("abort", onAbort);
  try {
    const redirectUri = `http://127.0.0.1:${(server.address() as AddressInfo).port}/callback`;
    const url = authorizeUrl(discovery, clientId, redirectUri, codeChallenge(verifier), state);
    ctx.stderr("Opening your browser to log in. If it doesn't open, visit:");
    ctx.stderr("");
    ctx.stderr(`  ${url}`);
    ctx.stderr("");
    ctx.openBrowser(url);
    ctx.stderr("Waiting for the login to finish in the browser…");
    return await exchangeCode(ctx.fetch, discovery.tokenEndpoint, clientId, await code, redirectUri, verifier, ctx.now);
  } finally {
    ctx.signal.removeEventListener("abort", onAbort);
    server.close();
  }
}

/** Device flow (RFC 8628), for SSH and headless machines: open a URL anywhere, approve, done. */
async function deviceFlow(ctx: CommandContext, discovery: Discovery, clientId: string): Promise<TokenSet> {
  const device = await startDevice(ctx.fetch, discovery.deviceAuthorizationEndpoint!, clientId);
  ctx.stderr("To log in, open this URL on any device and approve the request:");
  ctx.stderr("");
  ctx.stderr(`  ${device.verificationUriComplete ?? device.verificationUri}`);
  ctx.stderr(`  Code: ${device.userCode}`);
  ctx.stderr("");
  ctx.stderr("Waiting for approval…");
  return pollDevice(ctx.fetch, discovery.tokenEndpoint, clientId, device, { now: ctx.now, sleep: ctx.sleep, signal: ctx.signal });
}

export const loginGroup: CommandGroup = {
  name: "login",
  summary: "Log in to the server (browser, or a device code over SSH) and save the token in ~/.arm",
  defaultCommand: "login",
  commands: [
    {
      name: "login",
      summary: "Log in and save the token; later commands use it (and this server)",
      args: [],
      skipAuth: true,
      options: {
        device: { type: "boolean", description: "Use a device code (the default over SSH or without a display)" },
        browser: { type: "boolean", description: "Use the browser, even over SSH" },
      },
      async run(ctx) {
        if (ctx.options.device === true && ctx.options.browser === true) {
          throw new UsageError("--device and --browser are mutually exclusive");
        }
        if (!ctx.configDir) throw new UsageError("no folder for the token: set HOME or ARM_HOME");

        // 1. Which public OIDC client this server's users log in with.
        const auth = await unwrap(authConfigGet({ client: ctx.client }), ctx.client);
        if (!auth.authority || !auth.clientId) {
          throw new LoginError(`${ctx.url} has no login configured (Auth:Frontend); pass --token instead`);
        }

        // 2. Its endpoints, then the flow: a device code where there's no browser to redirect to.
        const discovery = await discover(ctx.fetch, auth.authority);
        const hasDevice = discovery.deviceAuthorizationEndpoint !== undefined;
        if (ctx.options.device === true && !hasDevice) {
          throw new LoginError("the login provider offers no device code flow; use --browser");
        }
        const device = hasDevice && ctx.options.browser !== true && (ctx.options.device === true || ctx.headless);
        const tokens = device
          ? await deviceFlow(ctx, discovery, auth.clientId)
          : await browserFlow(ctx, discovery, auth.clientId);

        // 3. Save the tokens for this server, and make it the default server.
        const server = serverKey(ctx.url);
        saveCredentials(ctx.configDir, {
          ...loadCredentials(ctx.configDir),
          [server]: { ...tokens, clientId: auth.clientId, tokenEndpoint: discovery.tokenEndpoint },
        });
        saveConfig(ctx.configDir, { ...loadConfig(ctx.configDir), url: server });

        const lines = [`Logged in to ${server}.`, `Token saved to ${credentialsPath(ctx.configDir)}.`];
        if (tokens.expiresAt) lines.push(`Access token expires ${tokens.expiresAt}; it is refreshed automatically.`);
        if (!tokens.refreshToken) lines.push("Warning: no refresh token was issued; run `arm login` again when it expires.");
        const summary = { url: server, authority: auth.authority, expiresAt: tokens.expiresAt ?? null, hasRefreshToken: !!tokens.refreshToken };
        if (ctx.json) ctx.stdout(JSON.stringify(summary, null, 2));
        else for (const line of lines) ctx.stdout(line);
        return undefined;
      },
    },
  ],
};

export const logoutGroup: CommandGroup = {
  name: "logout",
  summary: "Forget the saved token for the server",
  defaultCommand: "logout",
  commands: [
    {
      name: "logout",
      summary: "Remove this server's token from ~/.arm/credentials.json",
      args: [],
      skipAuth: true,
      options: {},
      async run(ctx) {
        if (!ctx.configDir) throw new UsageError("no config folder: set HOME or ARM_HOME");
        const server = serverKey(ctx.url);
        const { [server]: removed, ...rest } = loadCredentials(ctx.configDir);
        if (removed) saveCredentials(ctx.configDir, rest);
        const message = removed ? `Logged out of ${server}.` : `Not logged in to ${server}.`;
        return { json: { url: server, loggedOut: !!removed }, pretty: message };
      },
    },
  ],
};
