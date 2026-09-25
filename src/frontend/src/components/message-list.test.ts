import type { Message } from "@arm/client";
import { beforeAll, describe, expect, it, vi } from "vitest";
import { createApiClient } from "../api-client.js";
import { MessageList } from "./message-list.js";

/**
 * A real generated client over a scripted `fetch`: MessageList drives the Hey API SDK, and the
 * fake backend answers by method + path, recording every request it sees (URL, method, body).
 */
function fakeApi(overrides: { list?: () => Response; messages?: Message[] } = {}) {
  const calls: { method: string; path: string; search: string; body: unknown }[] = [];
  const messages = overrides.messages ?? [];

  const fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const request = new Request(input, init);
    const url = new URL(request.url);
    const text = await request.text();
    calls.push({
      method: request.method,
      path: url.pathname,
      search: url.search,
      body: text ? JSON.parse(text) : undefined,
    });

    if (request.method === "GET" && url.pathname === "/api/messages") {
      return (
        overrides.list?.() ??
        Response.json({
          items: messages,
          pageNumber: 1,
          pageSize: 20,
          totalCount: messages.length,
          hasNextPage: false,
        })
      );
    }
    if (request.method === "POST") return Response.json({ id: "new-id", text: "x" });
    if (request.method === "PUT") return Response.json({ id: "x", text: "x" });
    if (request.method === "DELETE") return new Response(null, { status: 200 });
    return new Response(null, { status: 404 });
  });

  return { client: createApiClient("http://test", { fetch }), calls };
}

beforeAll(() => customElements.define(MessageList.tagName, MessageList));

function make(): MessageList {
  return document.createElement(MessageList.tagName) as MessageList;
}

const lists = (calls: { method: string; path: string }[]) =>
  calls.filter((c) => c.method === "GET" && c.path === "/api/messages");

describe("<message-list>", () => {
  it("renders the messages the client returns, with a count", async () => {
    const { client, calls } = fakeApi({
      messages: [
        { id: "aaaaaaaa-0000", text: "hello" },
        { id: "bbbbbbbb-1111", text: "world" },
      ],
    });

    const el = make();
    el.client = client;
    await el.load();

    const texts = [...el.shadowRoot!.querySelectorAll(".text")].map((n) => n.textContent);
    expect(texts).toEqual(["hello", "world"]);
    expect(el.shadowRoot!.querySelector(".count")?.textContent).toContain("2 messages");
    expect(calls[0]?.search).toBe("?page=1&pageSize=20");
  });

  it("shows the empty state when there are no messages", async () => {
    const el = make();
    el.client = fakeApi().client;
    await el.load();
    expect(el.shadowRoot!.querySelector(".muted")?.textContent).toMatch(/No messages yet/);
  });

  it("surfaces an auth-specific error on 401", async () => {
    const el = make();
    el.client = fakeApi({ list: () => new Response(null, { status: 401 }) }).client;
    await el.load();
    expect(el.shadowRoot!.querySelector(".error")?.textContent).toMatch(/Authentication required/);
  });

  it("surfaces the API's problem message on other errors", async () => {
    const el = make();
    el.client = fakeApi({
      list: () => Response.json([{ message: "page must be >= 1" }], { status: 400 }),
    }).client;
    await el.load();
    expect(el.shadowRoot!.querySelector(".error")?.textContent).toBe("page must be >= 1");
  });

  it("create() posts the body then reloads", async () => {
    const { client, calls } = fakeApi();
    const el = make();
    el.client = client;
    await el.load();
    calls.length = 0;

    await el.create("new message");

    expect(calls[0]).toMatchObject({
      method: "POST",
      path: "/api/messages",
      body: { text: "new message" },
    });
    expect(lists(calls)).toHaveLength(1); // reloaded after the write
  });

  it("saveEdit() puts the body by id then reloads", async () => {
    const { client, calls } = fakeApi();
    const el = make();
    el.client = client;
    await el.load();
    calls.length = 0;

    await el.saveEdit("aaaaaaaa-0000", "edited");

    expect(calls[0]).toMatchObject({
      method: "PUT",
      path: "/api/messages/aaaaaaaa-0000",
      body: { text: "edited" },
    });
    expect(lists(calls)).toHaveLength(1);
  });

  it("deleteMessage() deletes by id then reloads", async () => {
    const { client, calls } = fakeApi();
    const el = make();
    el.client = client;
    await el.load();
    calls.length = 0;

    await el.deleteMessage("aaaaaaaa-0000");

    expect(calls[0]).toMatchObject({ method: "DELETE", path: "/api/messages/aaaaaaaa-0000" });
    expect(lists(calls)).toHaveLength(1);
  });
});
