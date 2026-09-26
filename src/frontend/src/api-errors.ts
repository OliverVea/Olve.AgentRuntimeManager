/**
 * A failed API call: the HTTP status (undefined when no response arrived, e.g. a network error)
 * and the parsed error body — the API's `{ error: { code, message, details } }` envelope.
 */
export class ApiError extends Error {
  constructor(
    readonly status: number | undefined,
    readonly body: unknown,
  ) {
    super(envelopeMessage(body) ?? (status ? `Request failed (${status}).` : "Request failed."));
    this.name = "ApiError";
  }

  /** The envelope's `error.code`, e.g. `QUEUE_FULL`. */
  get code(): string | undefined {
    const code = (this.body as { error?: { code?: unknown } } | undefined)?.error?.code;
    return typeof code === "string" ? code : undefined;
  }
}

/**
 * The generated SDK returns `{ data, error, response }` instead of throwing (and a thrown error
 * would carry only the body, not the status). Turn a failure into an {@link ApiError} with the
 * status, so callers can use plain try/catch.
 */
export async function unwrap<T>(
  call: Promise<{ data?: T; error?: unknown; response?: Response }>,
): Promise<T> {
  const { data, error, response } = await call;
  if (error !== undefined || !response?.ok) throw new ApiError(response?.status, error);
  return data as T;
}

/** The `error.message` of an error envelope, or a thrown Error's own message. */
function envelopeMessage(body: unknown): string | undefined {
  const message = (body as { error?: { message?: unknown } } | undefined)?.error?.message;
  if (typeof message === "string" && message) return message;
  if (body instanceof Error && body.message) return body.message;
  return undefined;
}

/** Turn a failed call into a human-readable line, calling out the auth case. */
export function describeError(error: unknown): string {
  const status = error instanceof ApiError ? error.status : undefined;
  if (status === 401 || status === 403) return "Not authorized — try logging in again.";
  return (error as { message?: string } | undefined)?.message || "Request failed.";
}
