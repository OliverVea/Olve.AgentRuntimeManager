import { type ProviderHealth, providersHealthList } from "@arm/client";
import { unwrap } from "../api";
import { formatTable, localDateTime } from "../output";
import type { CommandGroup } from "../registry";

/** One row per provider; `since`/`until` only when it isn't available. */
export function formatHealth(health: readonly ProviderHealth[]): string {
  if (health.length === 0) return "No providers.";
  return formatTable(health, [
    { header: "provider", value: (h: ProviderHealth) => h.provider },
    { header: "status", value: (h: ProviderHealth) => h.status },
    { header: "since", value: (h: ProviderHealth) => localDateTime(h.since) },
    { header: "until", value: (h: ProviderHealth) => localDateTime(h.until) },
    { header: "reason", value: (h: ProviderHealth) => h.reason },
  ]);
}

export const providerGroup: CommandGroup = {
  name: "provider",
  summary: "The providers that run agents",
  defaultCommand: "health",
  commands: [
    {
      name: "health",
      summary: "Whether each provider starts sessions now, and if not why and until when",
      args: [],
      options: {},
      async run({ client }) {
        const health = await unwrap(providersHealthList({ client }), client);
        return { json: health, pretty: formatHealth(health) };
      },
    },
  ],
};
