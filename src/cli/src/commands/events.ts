import { type ArmEventData, eventsStream } from "@arm/client";
import type { Client } from "@arm/client/client";
import { ApiError, NetworkError } from "../errors";
import { commaList } from "../options";
import { truncate } from "../output";
import type { CommandGroup } from "../registry";

/** One received event: the SSE name and id (none for heartbeats) and its typed data. */
export type ReceivedEvent = {
  event: string;
  id?: string;
  data: ArmEventData;
};

/** Tunables, exported so tests can shorten the pauses. */
export const tailSettings = {
  /** Pause before reconnecting after the server ends the stream (e.g. a redeploy). */
  reconnectDelayMs: 1000,
  /** First retry delay after a lost connection; the SSE client doubles it per attempt (max 30s). */
  retryDelayMs: 3000,
};

/**
 * Tails `GET /api/events` with the generated Hey API SSE client until `signal` aborts, calling
 * `onEvent` for each event. Around the generated client:
 * - a 4xx (bad filter, expired token), or any failure of the first connection, throws
 *   {@link ApiError} / {@link NetworkError} instead of the client's silent retry-forever;
 * - once connected, a dropped connection or a 5xx (the edge's 502/503 while a redeploy is under
 *   way) is retried by the client (with backoff and `Last-Event-ID`), and a stream the server
 *   ends is reopened here from the last event id;
 * - each event's own id is recovered: the client reports the last id seen, so a heartbeat
 *   (which has none) would otherwise appear to repeat the previous event's.
 */
export async function tailEvents(options: {
  client: Client;
  event?: string[];
  excludeEvent?: string[];
  lastEventId?: string;
  signal: AbortSignal;
  onEvent(event: ReceivedEvent): void;
  onWarning(message: string): void;
}): Promise<void> {
  const { client, signal } = options;
  const stop = new AbortController();
  const onAbort = () => stop.abort();
  signal.addEventListener("abort", onAbort, { once: true });
  if (signal.aborted) stop.abort();

  const baseFetch = client.getConfig().fetch ?? globalThis.fetch;
  let failure: Error | undefined;
  let connected = false;
  let lastEventId = options.lastEventId;

  const guardedFetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const request = input instanceof Request ? input : new Request(input, init);
    // Ctrl+C aborts the request only while connecting. Once the response is here, the SSE client
    // cancels the body itself on abort; aborting the fetch too would error the body underneath
    // it, and the client's (unawaited) cancel() would reject unhandled (Bun: exit 1).
    const connecting = new AbortController();
    const onStop = () => connecting.abort();
    stop.signal.addEventListener("abort", onStop, { once: true });
    if (stop.signal.aborted) connecting.abort();
    let response: Response;
    try {
      response = await baseFetch(new Request(request, { signal: connecting.signal }));
    } catch (error) {
      if (!connected && !stop.signal.aborted) {
        failure = new NetworkError(request.url, error);
        stop.abort();
      }
      throw error;
    } finally {
      stop.signal.removeEventListener("abort", onStop);
    }
    if (response.ok) {
      connected = true;
    } else if (!connected || response.status < 500) {
      failure = new ApiError(response.status, response.statusText, await readBody(response));
      stop.abort();
    }
    // A 5xx after a successful connection falls through: the SSE client throws on it, warns
    // (onSseError) and retries with backoff.
    return response;
  }) as typeof fetch;

  try {
    while (!stop.signal.aborted) {
      let meta: { event: string; id?: string } = { event: "message" };
      let previousId: string | undefined;
      const { stream } = await eventsStream({
        client,
        query: { event: options.event, exclude_event: options.excludeEvent },
        headers: {
          Accept: "text/event-stream",
          ...(lastEventId ? { "Last-Event-ID": lastEventId } : {}),
        },
        signal: stop.signal,
        fetch: guardedFetch,
        sseDefaultRetryDelay: tailSettings.retryDelayMs,
        // Honoured by the SSE client but missing from the typed request options: without it,
        // a failed request (aborted above) would still wait out the retry backoff before ending.
        ...({ sseSleepFn: (ms: number) => sleep(ms, stop.signal) } as object),
        onSseEvent: (e) => {
          meta = { event: e.event ?? "message", id: e.id !== previousId ? e.id : undefined };
          previousId = e.id;
        },
        onSseError: (error) => {
          if (connected && !stop.signal.aborted) {
            options.onWarning(`connection lost (${describe(error)}); reconnecting`);
          }
        },
      });

      for await (const data of stream) {
        if (meta.id) lastEventId = meta.id;
        options.onEvent({ ...meta, data: data as ArmEventData });
      }

      if (!stop.signal.aborted) {
        options.onWarning("the server ended the stream; reconnecting");
        await sleep(tailSettings.reconnectDelayMs, stop.signal);
      }
    }
  } finally {
    signal.removeEventListener("abort", onAbort);
  }

  if (failure) throw failure;
}

/** `12:03:04 session.created <sessionId> "prompt"`: one readable line per event (local time). */
export function formatEvent(e: ReceivedEvent): string {
  const data = e.data;
  const head = `${clock(data.at)} ${data.type}`;
  switch (data.type) {
    case "heartbeat":
      return head;
    case "session.created":
      return `${head} ${data.sessionId} ${JSON.stringify(truncate(data.session.prompt, 60))}`;
    case "session.queued":
      return `${head} ${data.sessionId} position=${data.position}`;
    case "session.started":
      return `${head} ${data.sessionId} providerSessionId=${data.providerSessionId}`;
    case "session.completed":
      return `${head} ${data.sessionId} exitCode=${data.exitCode}${data.summary ? ` ${JSON.stringify(truncate(data.summary, 60))}` : ""}`;
    case "session.failed":
      return `${head} ${data.sessionId} ${JSON.stringify(truncate(data.error, 60))}`;
    case "session.killed":
      return `${head} ${data.sessionId} source=${data.source}${data.caller ? ` caller=${data.caller}` : ""}${data.reason ? ` ${JSON.stringify(truncate(data.reason, 60))}` : ""}`;
    default: {
      // An event this CLI doesn't know yet: its name and the rest of its data.
      const { type, at, ...rest } = data as { type?: string; at?: string };
      return `${clock(at)} ${type ?? e.event} ${JSON.stringify(rest)}`;
    }
  }
}

/** NDJSON: `{"event":…,"id":…,"data":{…}}`, the id omitted for events without one (heartbeats). */
export function formatEventJson(e: ReceivedEvent): string {
  return JSON.stringify(e.id ? { event: e.event, id: e.id, data: e.data } : { event: e.event, data: e.data });
}

function clock(at: string | undefined): string {
  const date = at ? new Date(at) : undefined;
  if (!date || Number.isNaN(date.getTime())) return "--:--:--";
  return [date.getHours(), date.getMinutes(), date.getSeconds()].map((n) => String(n).padStart(2, "0")).join(":");
}

function sleep(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve) => {
    if (signal.aborted) return resolve();
    const done = () => {
      clearTimeout(timer);
      signal.removeEventListener("abort", done);
      resolve();
    };
    const timer = setTimeout(done, ms);
    signal.addEventListener("abort", done, { once: true });
  });
}

async function readBody(response: Response): Promise<unknown> {
  const text = await response.text().catch(() => "");
  if (!text) return {};
  try {
    return JSON.parse(text);
  } catch {
    return text;
  }
}

function describe(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

export const eventsGroup: CommandGroup = {
  name: "events",
  summary: "Tail the event stream (Ctrl+C to stop)",
  defaultCommand: "tail",
  commands: [
    {
      name: "tail",
      summary:
        "Tail the event stream until Ctrl+C: one line per event (heartbeats hidden), or NDJSON with --json (heartbeats included). Reconnects with the last event id",
      args: [],
      streaming: true,
      options: {
        event: {
          type: "string",
          description: "Only these events, comma-separated; 'session.*' matches a namespace",
          valueName: "X,Y",
        },
        "exclude-event": {
          type: "string",
          description: "All events except these (same names as --event)",
          valueName: "X,Y",
        },
        "last-event-id": {
          type: "string",
          description: "First replay the retained events after this id (from --json output)",
          valueName: "ID",
        },
      },
      async run({ options, client, json, stdout, stderr, signal }) {
        await tailEvents({
          client,
          event: commaList(options, "event"),
          excludeEvent: commaList(options, "exclude-event"),
          lastEventId: (options["last-event-id"] as string | undefined) || undefined,
          signal,
          onEvent: (e) => {
            if (json) stdout(formatEventJson(e));
            else if (e.data.type !== "heartbeat") stdout(formatEvent(e));
          },
          onWarning: (message) => stderr(`warning: ${message}`),
        });
        return undefined;
      },
    },
  ],
};
