import "@typespec/http";

using Http;

namespace Arm;

/**
 * Whether ARM starts sessions on a provider. New sessions are queued either way; only
 * `available` starts them.
 */
enum ProviderStatus {
  available,

  /** Out of usage (e.g. a subscription's session limit); starts again at `until`. */
  limited,

  /** The provider's API failed (outage, overload, network); one queued session tries again at `until`. */
  unreachable,

  /** The provider's credentials are missing or were rejected; waits for a restart with new ones. */
  unauthorized,
}

/** A provider's health, as its last session (or startup) left it. */
model ProviderHealth {
  /** The provider's name, as sessions give it (`claude`, `fake`). */
  provider: string;

  status: ProviderStatus;

  /** Why it isn't `available`: the provider's own message. Set when status isn't `available`. */
  reason?: string;

  /** When it entered this status. Set when status isn't `available`. */
  since?: utcDateTime;

  /** When sessions start again (`limited`) or the next try is (`unreachable`). */
  until?: utcDateTime;
}

/** A model a provider offers. */
model ProviderModel {
  /** What a session gives as `model`. */
  id: string;

  /** Its name for people, e.g. `Opus`; the id when the server's configuration gives none. */
  displayName: string;
}

/** A provider this server has, and the models it offers. */
model Provider {
  /** What a session gives as `provider`: `claude`, `fake`. */
  name: string;

  /** Its name for people, e.g. `Claude Code`; the name when the configuration gives none. */
  displayName: string;

  /** The models sessions may name for it, as configured on this server. */
  models: ProviderModel[];
}

@route("/providers")
interface Providers {
  /** Every provider this server has, with its models. */
  @get list(): Provider[] | UnauthorizedResponse;
}

@route("/providers/health")
interface ProvidersHealth {
  /** The health of every provider this server has. */
  @get list(): ProviderHealth[] | UnauthorizedResponse;
}

/** A provider's health changed; carries the new health. */
model ProviderHealthChanged {
  ...Event<"provider.health">;
  health: ProviderHealth;
}
