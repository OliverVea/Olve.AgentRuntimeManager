import { createClient, createConfig, type Client } from "@arm/client/client";
import { ApiError, NetworkError } from "./errors";

export type ApiSettings = {
  baseUrl: string;
  token?: string;
  fetch?: typeof fetch;
};

/** One client per invocation, so tests (and future multi-server use) never share global state. */
export function createApiClient(settings: ApiSettings): Client {
  const headers: Record<string, string> = { Accept: "application/json" };
  // The contract declares no security scheme yet, so the client's `auth` hook never fires;
  // send the bearer explicitly on every request instead.
  if (settings.token) headers.Authorization = `Bearer ${settings.token}`;

  return createClient(
    createConfig({
      baseUrl: settings.baseUrl.replace(/\/+$/, ""),
      headers,
      ...(settings.fetch ? { fetch: settings.fetch } : {}),
    }),
  );
}

type SdkResult<T> = {
  data?: T;
  error?: unknown;
  request?: Request;
  response?: Response;
};

/**
 * Unwraps a Hey API call: returns the data on 2xx, throws {@link ApiError} on a non-2xx response
 * and {@link NetworkError} when no response arrived at all.
 */
export async function unwrap<T>(call: Promise<SdkResult<T>>, client: Client): Promise<T> {
  const result = await call;
  if (!result.response) {
    const url = result.request?.url ?? client.getConfig().baseUrl ?? "(unknown)";
    throw new NetworkError(url, result.error);
  }
  if (!result.response.ok) {
    throw new ApiError(result.response.status, result.response.statusText, result.error);
  }
  return result.data as T;
}
