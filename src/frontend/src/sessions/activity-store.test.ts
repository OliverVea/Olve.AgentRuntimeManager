import type { ActivityLine, Session } from "@arm/client";
import { afterEach, describe, expect, it, vi } from "vitest";
import { fakeApi, session } from "../testing/fake-api.js";
import { ACTIVITY_LINES, ActivityStore } from "./activity-store.js";

/** The working sessions, as the session store gives them, and changed by hand. */
class Sessions extends EventTarget {
  constructor(public active: Session[]) {
    super();
  }
  set(active: Session[]): void {
    this.active = active;
    this.dispatchEvent(new Event("change"));
  }
}

/** A page that can be hidden and shown. */
class Page extends EventTarget {
  hidden = false;
  set(hidden: boolean): void {
    this.hidden = hidden;
    this.dispatchEvent(new Event("visibilitychange"));
  }
}

const INTERVAL = 20;
const stores: ActivityStore[] = [];
afterEach(() => {
  for (const store of stores.splice(0)) store.stop();
});

function start(api: ReturnType<typeof fakeApi>, sessions: Sessions, page = new Page()) {
  const store = new ActivityStore(api.client, sessions, {
    intervalMs: INTERVAL,
    document: page as unknown as Document,
  });
  stores.push(store);
  store.start();
  return store;
}

const line = (text: string): ActivityLine => ({ kind: "text", text });
/** Long enough for several reads at INTERVAL, bounded so nothing waits forever. */
const settle = () => new Promise((resolve) => setTimeout(resolve, INTERVAL * 5));

describe("ActivityStore", () => {
  it("reads every working session's latest lines, not queued ones, and says so", async () => {
    const api = fakeApi({ activity: (id) => [line(`said by ${id}`)] });
    const sessions = new Sessions([session("w"), session("q", { status: "queued" })]);
    const store = start(api, sessions);
    const changed = vi.fn();
    store.addEventListener("change", changed);

    await vi.waitFor(() => expect(store.lines("w")).toEqual([line("said by w")]));
    expect(changed).toHaveBeenCalled();
    expect(store.lines("q")).toBeUndefined();
    expect(api.activityReads("q")).toHaveLength(0);
    expect(api.activityReads("w")[0]?.search).toBe(`?limit=${ACTIVITY_LINES}`);
  });

  it("re-reads every few seconds while the session works, and stops once it has ended", async () => {
    let said = 1;
    const api = fakeApi({ activity: () => [line(`line ${said}`)] });
    const sessions = new Sessions([session("w")]);
    const store = start(api, sessions);

    await vi.waitFor(() => expect(api.activityReads("w").length).toBeGreaterThanOrEqual(3));
    said = 2;
    await vi.waitFor(() => expect(store.lines("w")).toEqual([line("line 2")]));

    sessions.set([session("w", { status: "completed" })]);
    expect(store.lines("w")).toBeUndefined();
    const reads = api.activityReads("w").length;
    await settle();
    // At most the read that was in flight when it ended.
    expect(api.activityReads("w").length).toBeLessThanOrEqual(reads + 1);
    expect(store.lines("w")).toBeUndefined();
  });

  it("reads a session that starts working at once", async () => {
    const api = fakeApi({ activity: () => [line("hi")] });
    const sessions = new Sessions([]);
    const store = start(api, sessions);
    await settle();
    expect(api.activityReads()).toHaveLength(0);

    sessions.set([session("w")]);

    await vi.waitFor(() => expect(store.lines("w")).toEqual([line("hi")]));
  });

  it("stops while the page is hidden and reads again when it's shown", async () => {
    const api = fakeApi({ activity: () => [line("hi")] });
    const page = new Page();
    start(api, new Sessions([session("w")]), page);
    await vi.waitFor(() => expect(api.activityReads().length).toBeGreaterThanOrEqual(2));

    page.set(true);
    await settle();
    const reads = api.activityReads().length;
    await settle();
    expect(api.activityReads()).toHaveLength(reads);

    page.set(false);
    await vi.waitFor(() => expect(api.activityReads().length).toBeGreaterThan(reads));
  });

  it("stops on stop(), keeping the lines it read", async () => {
    const api = fakeApi({ activity: () => [line("hi")] });
    const store = start(api, new Sessions([session("w")]));
    await vi.waitFor(() => expect(store.lines("w")).toEqual([line("hi")]));

    store.stop();
    await settle();
    const reads = api.activityReads().length;
    await settle();

    expect(api.activityReads()).toHaveLength(reads);
    expect(store.lines("w")).toEqual([line("hi")]);
  });

  it("keeps the lines it had when a read fails, and tries again", async () => {
    let up = true;
    const api = fakeApi({ activity: () => (up ? [line("hi")] : undefined) });
    const store = start(api, new Sessions([session("w")]));
    await vi.waitFor(() => expect(store.lines("w")).toEqual([line("hi")]));

    up = false;
    const reads = api.activityReads().length;
    await vi.waitFor(() => expect(api.activityReads().length).toBeGreaterThan(reads + 1));

    expect(store.lines("w")).toEqual([line("hi")]);
  });

  it("says nothing when a re-read brings the same lines", async () => {
    const api = fakeApi({ activity: () => [line("same")] });
    const store = start(api, new Sessions([session("w")]));
    await vi.waitFor(() => expect(store.lines("w")).toBeDefined());
    const changed = vi.fn();
    store.addEventListener("change", changed);

    const reads = api.activityReads().length;
    await vi.waitFor(() => expect(api.activityReads().length).toBeGreaterThan(reads + 1));

    expect(changed).not.toHaveBeenCalled();
  });
});
