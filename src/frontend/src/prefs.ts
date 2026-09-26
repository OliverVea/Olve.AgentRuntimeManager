/**
 * What this browser remembers, like `arm config` on a machine: defaults for new sessions, the
 * theme, and how times are shown. Storage can be missing or throw (private windows, blocked site
 * data), so every access is guarded and the app works with the defaults.
 */

export type Theme = "light" | "dark";

export type SessionDefaults = {
  /** `provider/model`, as the composer takes it. */
  model: string;
  /** Empty: the logged-in user's name. */
  caller: string;
  /** Unset: no timeout. */
  timeoutSeconds?: number;
};

export type Prefs = {
  defaults: SessionDefaults;
  /** Unset: follow the system. */
  theme?: Theme;
  /** Times after the status: running time (`relative`) or clock times (`absolute`). */
  times: "relative" | "absolute";
};

const key = "arm-prefs";

export const initialPrefs: Prefs = {
  defaults: { model: "fake/fake", caller: "" },
  times: "relative",
};

export function loadPrefs(storage: Storage | undefined = safeStorage()): Prefs {
  try {
    const raw = storage?.getItem(key);
    if (!raw) return initialPrefs;
    const saved = JSON.parse(raw) as Partial<Prefs>;
    return {
      ...initialPrefs,
      ...saved,
      defaults: { ...initialPrefs.defaults, ...saved.defaults },
    };
  } catch {
    return initialPrefs;
  }
}

export function savePrefs(prefs: Prefs, storage: Storage | undefined = safeStorage()): void {
  try {
    storage?.setItem(key, JSON.stringify(prefs));
  } catch {
    // Not remembered; this page still uses them.
  }
}

function safeStorage(): Storage | undefined {
  try {
    return window.localStorage;
  } catch {
    return undefined;
  }
}
