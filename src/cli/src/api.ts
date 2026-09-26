import { createClient, createConfig, type Client } from "@arm/client/client";
import { ApiError, NetworkError } from "./errors";

export type ApiSettings = {
  baseUrl: string;
  token?: string;
  fetch?: typeof fetch;
};

/** One client per invocation, so tests (and future multi-server use) never share global state. */
export function createApiClient(settings: ApiSettings): Client {
  return createClient(
    createConfig({
      baseUrl: settings.baseUrl.replace(/\/+$/, ""),
      headers: { Accept: "application/json" },
      // The contract declares bearer auth (`@useAuth(BearerAuth)`), so every secured operation
      // (including the SSE stream) runs the client's `auth` hook, which sends `Authorization:
      // Bearer <token>`; public ones (`@useAuth(NoAuth)`, e.g. auth-config) never get it.
      auth: settings.token,
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
 * Sends a Hey API call: returns the data and the HTTP status on 2xx (for operations whose success
 * statuses differ in meaning, like 201 started vs 202 queued), throws {@link ApiError} on a
 * non-2xx response and {@link NetworkError} when no response arrived at all.
 */
export async function send<T>(
  call: Promise<SdkResult<T>>,
  client: Client,
): Promise<{ data: T; status: number }> {
  const result = await call;
  if (!result.response) {
    const url = result.request?.url ?? client.getConfig().baseUrl ?? "(unknown)";
    throw new NetworkError(url, result.error);
  }
  if (!result.response.ok) {
    throw new ApiError(result.response.status, result.response.statusText, result.error);
  }
  return { data: result.data as T, status: result.response.status };
}

/** Like {@link send}, for when only the data matters. */
export async function unwrap<T>(call: Promise<SdkResult<T>>, client: Client): Promise<T> {
  return (await send(call, client)).data;
}
