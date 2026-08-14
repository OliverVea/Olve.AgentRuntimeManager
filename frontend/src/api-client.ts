import {
  AnonymousAuthenticationProvider,
  type AuthenticationProvider,
  type RequestInformation,
} from "@microsoft/kiota-abstractions";
import { FetchRequestAdapter, HttpClient } from "@microsoft/kiota-http-fetchlibrary";
import {
  createOlveTemplateApiClient,
  type OlveTemplateApiClient,
} from "./api/olveTemplateApiClient.js";

/** Supplies the current bearer token (async — it may refresh), or nullish for anonymous. */
export type TokenSource = () => Promise<string | null | undefined>;

/** Called on a 401: refresh the credential and return a new token, or null to give up. */
export type UnauthorizedHandler = () => Promise<string | null>;

/**
 * Attaches `Authorization: Bearer <token>` when a token is available, and nothing otherwise — so
 * anonymous `GET /api/messages` keeps working while authenticated writes get a token the moment
 * one is provided. The token getter is async so it can refresh a near-expiry token just in time.
 */
class BearerTokenAuthenticationProvider implements AuthenticationProvider {
  constructor(private readonly getToken: TokenSource) {}

  public authenticateRequest = async (request: RequestInformation): Promise<void> => {
    const token = await this.getToken();
    if (token) {
      request.headers.tryAdd("Authorization", `Bearer ${token}`);
    }
  };
}

/**
 * Build the Kiota client. Pass `getToken` to enable authenticated writes; `onUnauthorized` adds a
 * 401 → refresh → retry-once safety net (for the rare case a token is revoked or expires between
 * the proactive refresh and the request). Omit both for a purely anonymous client.
 */
export function createClient(
  baseUrl: string,
  opts: { getToken?: TokenSource; onUnauthorized?: UnauthorizedHandler } = {},
): OlveTemplateApiClient {
  const { getToken, onUnauthorized } = opts;

  const authProvider: AuthenticationProvider = getToken
    ? new BearerTokenAuthenticationProvider(getToken)
    : new AnonymousAuthenticationProvider();

  // Terminal fetch: on a 401, refresh once and replay the request with the new bearer. Default
  // Kiota middleware (retry/redirect) still wraps this — it just calls us as the final send.
  const authFetch = async (url: string, init: RequestInit): Promise<Response> => {
    let response = await fetch(url, init);
    if (response.status === 401 && onUnauthorized) {
      const fresh = await onUnauthorized();
      if (fresh) {
        const headers = new Headers(init.headers);
        headers.set("Authorization", `Bearer ${fresh}`);
        response = await fetch(url, { ...init, headers });
      }
    }
    return response;
  };

  const httpClient = new HttpClient(authFetch);
  const adapter = new FetchRequestAdapter(authProvider, undefined, undefined, httpClient);
  adapter.baseUrl = baseUrl;
  return createOlveTemplateApiClient(adapter);
}
