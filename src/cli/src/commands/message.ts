import {
  messagesCreate,
  messagesDelete,
  messagesGet,
  messagesList,
  messagesUpdate,
  type Message,
  type PageOfMessage,
} from "@arm/client";
import { unwrap } from "../api";
import { UsageError } from "../errors";
import { formatObject, formatTable } from "../output";
import type { CommandGroup, OptionValues } from "../registry";

function positiveInt(options: OptionValues, name: string): number | undefined {
  const raw = options[name];
  if (raw === undefined) return undefined;
  const text = String(raw);
  if (!/^\d+$/.test(text) || Number(text) < 1) {
    throw new UsageError(`--${name} must be a positive integer, got '${text}'`);
  }
  return Number(text);
}

function formatPage(page: PageOfMessage): string {
  if (page.items.length === 0) {
    return page.totalCount > 0
      ? `No messages on page ${page.pageNumber} (${page.totalCount} total).`
      : "No messages.";
  }
  const table = formatTable(page.items, [
    { header: "id", value: (m: Message) => m.id },
    { header: "text", value: (m: Message) => m.text },
  ]);
  const pages = page.totalPages ?? Math.max(1, Math.ceil(page.totalCount / page.pageSize));
  let footer = `Page ${page.pageNumber} of ${pages} · ${page.totalCount} message${page.totalCount === 1 ? "" : "s"}`;
  if (page.hasNextPage ?? page.pageNumber < pages) footer += ` · next: --page ${page.pageNumber + 1}`;
  return `${table}\n\n${footer}`;
}

export const messageGroup: CommandGroup = {
  name: "message",
  summary: "Manage messages (the M1 example entity)",
  commands: [
    {
      name: "list",
      summary: "List messages, one page at a time",
      args: [],
      options: {
        page: { type: "string", description: "Page number, 1-based (default 1)", valueName: "N" },
        "page-size": { type: "string", description: "Items per page (default 20, max 100)", valueName: "N" },
      },
      async run({ options, client }) {
        const query = { page: positiveInt(options, "page"), pageSize: positiveInt(options, "page-size") };
        const page = await unwrap(messagesList({ client, query }), client);
        return { json: page, pretty: formatPage(page) };
      },
    },
    {
      name: "get",
      summary: "Show one message",
      args: [{ name: "id", description: "Message ID" }],
      options: {},
      async run({ args, client }) {
        const message = await unwrap(messagesGet({ client, path: { id: args.id! } }), client);
        return { json: message, pretty: formatObject(message) };
      },
    },
    {
      name: "create",
      summary: "Create a message",
      args: [{ name: "text", description: "Message text (max 280 characters)" }],
      options: {},
      async run({ args, client }) {
        const message = await unwrap(messagesCreate({ client, body: { text: args.text! } }), client);
        return { json: message, pretty: formatObject(message) };
      },
    },
    {
      name: "update",
      summary: "Replace a message's text",
      args: [
        { name: "id", description: "Message ID" },
        { name: "text", description: "New text (max 280 characters)" },
      ],
      options: {},
      async run({ args, client }) {
        const message = await unwrap(
          messagesUpdate({ client, path: { id: args.id! }, body: { text: args.text! } }),
          client,
        );
        return { json: message, pretty: formatObject(message) };
      },
    },
    {
      name: "delete",
      summary: "Delete a message",
      args: [{ name: "id", description: "Message ID" }],
      options: {},
      async run({ args, client }) {
        const result = await unwrap(messagesDelete({ client, path: { id: args.id! } }), client);
        return { json: result ?? {}, pretty: `Deleted message ${args.id}` };
      },
    },
  ],
};
