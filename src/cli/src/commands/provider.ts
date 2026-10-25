import { type Provider, type ProviderHealth, providersHealthList, providersList } from "@arm/client";
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

type ModelRow = { provider: Provider; id?: string; displayName?: string };

/** One row per model (a provider without any still gets one), its provider's display name last. */
export function formatProviders(providers: readonly Provider[]): string {
  if (providers.length === 0) return "No providers.";
  const rows: ModelRow[] = providers.flatMap((provider) =>
    provider.models.length === 0 ? [{ provider }] : provider.models.map((m) => ({ provider, ...m })),
  );
  return formatTable(rows, [
    { header: "provider", value: (r: ModelRow) => r.provider.name },
    { header: "model", value: (r: ModelRow) => r.id },
    { header: "name", value: (r: ModelRow) => r.displayName },
    { header: "provider name", value: (r: ModelRow) => r.provider.displayName },
  ]);
}

export const providerGroup: CommandGroup = {
  name: "provider",
  summary: "The providers that run agents",
  defaultCommand: "health",
  commands: [
    {
      name: "list",
      summary: "Every provider and the models sessions may name for it (--model)",
      args: [],
      options: {},
      async run({ client }) {
        const providers = await unwrap(providersList({ client }), client);
        return { json: providers, pretty: formatProviders(providers) };
      },
    },
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
