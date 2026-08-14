import { beforeAll, describe, expect, it, vi } from "vitest";
import type { OlveTemplateApiClient } from "../api/olveTemplateApiClient.js";
import { MessageList } from "./message-list.js";

/**
 * A minimal fake of the Kiota client — MessageList only touches `api.messages.get/post/byId`,
 * so we stub exactly those and cast. `totalCount` is an `UntypedNode`-like `{ getValue }`.
 */
function fakeClient(overrides: {
  get?: () => Promise<unknown>;
  post?: (body: unknown) => Promise<unknown>;
  put?: (body: unknown) => Promise<unknown>;
  del?: () => Promise<unknown>;
}) {
  const put = vi.fn(overrides.put ?? (async () => ({})));
  const del = vi.fn(overrides.del ?? (async () => new ArrayBuffer(0)));
  const messages = {
    get: vi.fn(
      overrides.get ?? (async () => ({ items: [], totalCount: node(0), hasNextPage: false })),
    ),
    post: vi.fn(overrides.post ?? (async () => ({}))),
    byId: vi.fn(() => ({ put, delete: del })),
  };
  const client = { api: { messages } };
  return {
    client: client as unknown as OlveTemplateApiClient,
    spies: {
      get: messages.get,
      post: messages.post,
      byId: messages.byId,
      put,
      del,
    },
  };
}

const node = (value: number) => ({ getValue: () => value });

beforeAll(() => customElements.define(MessageList.tagName, MessageList));

function make(): MessageList {
  return document.createElement(MessageList.tagName) as MessageList;
}

describe("<message-list>", () => {
  it("renders the messages the client returns, with a count", async () => {
    const { client } = fakeClient({
      get: async () => ({
        items: [
          { id: "aaaaaaaa-0000", text: "hello" },
          { id: "bbbbbbbb-1111", text: "world" },
        ],
        totalCount: node(2),
        hasNextPage: false,
      }),
    });

    const el = make();
    el.client = client;
    await el.load();

    const texts = [...el.shadowRoot!.querySelectorAll(".text")].map((n) => n.textContent);
    expect(texts).toEqual(["hello", "world"]);
    expect(el.shadowRoot!.querySelector(".count")?.textContent).toContain("2 messages");
  });

  it("shows the empty state when there are no messages", async () => {
    const el = make();
    el.client = fakeClient({}).client;
    await el.load();
    expect(el.shadowRoot!.querySelector(".muted")?.textContent).toMatch(/No messages yet/);
  });

  it("surfaces an auth-specific error on 401", async () => {
    const el = make();
    el.client = fakeClient({
      get: async () => {
        throw { responseStatusCode: 401 };
      },
    }).client;
    await el.load();
    expect(el.shadowRoot!.querySelector(".error")?.textContent).toMatch(/Authentication required/);
  });

  it("create() posts the body then reloads", async () => {
    const { client, spies } = fakeClient({});
    const el = make();
    el.client = client;
    await el.load();
    spies.get.mockClear();

    await el.create("new message");

    expect(spies.post).toHaveBeenCalledWith({ text: "new message" });
    expect(spies.get).toHaveBeenCalledTimes(1); // reloaded after the write
  });

  it("deleteMessage() deletes by id then reloads", async () => {
    const { client, spies } = fakeClient({});
    const el = make();
    el.client = client;
    await el.load();
    spies.get.mockClear();

    await el.deleteMessage("aaaaaaaa-0000");

    expect(spies.byId).toHaveBeenCalledWith("aaaaaaaa-0000");
    expect(spies.del).toHaveBeenCalledTimes(1);
    expect(spies.get).toHaveBeenCalledTimes(1);
  });
});
