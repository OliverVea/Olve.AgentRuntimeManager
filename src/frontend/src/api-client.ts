import { type Client, createClient, createConfig } from "@arm/client/client";

/** Supplies the current bearer token (async — it may refresh), or nullish for anonymous. */
export type TokenSource = () => Promise<string | null | undefined>;

/** Called on a 401: refresh the credential and return a new token, or null to give up. */
export type UnauthorizedHandler = () => Promise<string | null>;

/**
 * Build a Hey API client for the generated SDK (`@arm/client`) — pass it as `client` to every
 * SDK call. `baseUrl` is the API origin (same-origin by default in `main.ts`).
 *
 * - `getToken` enables authenticated writes: a request interceptor attaches
 *   `Authorization: Bearer <token>` when a token is available and nothing otherwise, so anonymous
 *   `GET /api/messages` keeps working. The spec declares no security scheme, so the client's
 *   `auth` option would never fire — the interceptor is the hook.
 * - `onUnauthorized` adds a 401 → refresh → retry-once safety net (for the rare case a token is
 *   revoked or expires between the proactive refresh and the request).
 *
 * Omit both for a purely anonymous client.
 */
export function createApiClient(
  baseUrl: string,
  opts: {
    getToken?: TokenSource;
    onUnauthorized?: UnauthorizedHandler;
    fetch?: typeof fetch;
  } = {},
): Client {
  const { getToken, onUnauthorized } = opts;
  const send = opts.fetch ?? ((input: RequestInfo | URL, init?: RequestInit) => fetch(input, init));

  // Terminal fetch: on a 401, refresh once and replay the request with the new bearer. The
  // request is cloned up front because the first send consumes its body, and interceptors don't
  // run again for the replay — so the fresh bearer is set on the clone here.
  const authFetch = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const request = new Request(input, init);
    const replay = onUnauthorized ? request.clone() : undefined;
    const response = await send(request);
    if (response.status !== 401 || !onUnauthorized || !replay) return response;

    const fresh = await onUnauthorized();
    if (!fresh) return response;
    replay.headers.set("Authorization", `Bearer ${fresh}`);
    return send(replay);
  };

  const client = createClient(createConfig({ baseUrl, fetch: authFetch }));

  if (getToken) {
    client.interceptors.request.use(async (request) => {
      const token = await getToken();
      if (token) request.headers.set("Authorization", `Bearer ${token}`);
      return request;
    });
  }

  return client;
}
