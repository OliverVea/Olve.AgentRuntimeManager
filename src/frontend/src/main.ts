import "./styles.css";
import {
  type Session,
  serverInfoApiGet,
  sessionsCreate,
  sessionsDelete,
  sessionsGet,
  sessionsKill,
} from "@arm/client";
import { createApiClient } from "./api-client.js";
import { describeError, unwrap } from "./api-errors.js";
import * as auth from "./auth/oidc.js";
import { escapeHtml } from "./base-element.js";
import { SessionList, type View } from "./components/session-list.js";
import { loadPrefs, type Prefs, savePrefs, type Theme } from "./prefs.js";
import { parseModel, shortId } from "./sessions/format.js";
import { newSession, parseTimeout } from "./sessions/new-session.js";
import { SessionStore, type StreamState } from "./sessions/session-store.js";

// In dev, same-origin → Vite proxies /api to the backend (see vite.config.ts). In a build, set
// VITE_API_BASE_URL to point at the API host (defaults to same-origin, which is how we deploy).
const baseUrl = import.meta.env.VITE_API_BASE_URL ?? window.location.origin;

// One client for the whole app. getAccessToken() returns a fresh bearer when logged in (null when
// logged out); onUnauthorized refreshes + retries on a 401.
const client = createApiClient(baseUrl, {
  getToken: auth.getAccessToken,
  onUnauthorized: auth.refresh,
});
const store = new SessionStore(client);
let prefs: Prefs = loadPrefs();

const $ = <E extends HTMLElement = HTMLElement>(selector: string) => {
  const el = document.querySelector<E>(selector);
  if (!el) throw new Error(`missing ${selector}`);
  return el;
};
const icon = (name: string) => `<svg class="i"><use href="#i-${name}"/></svg>`;

customElements.define(SessionList.tagName, SessionList);
const list = $<SessionList>("#list");
list.store = store;
list.times = prefs.times;

// --- theme: the system's, unless Options picks light or dark ------------------------------------
const systemDark = matchMedia("(prefers-color-scheme: dark)");
const isDark = () => (prefs.theme ? prefs.theme === "dark" : systemDark.matches);

function applyTheme(): void {
  const dark = isDark();
  document.documentElement.classList.toggle("dark", dark);
  const button = $("#theme");
  button.innerHTML = icon(dark ? "sun" : "moon");
  button.title = dark ? "Switch to light" : "Switch to dark";
  button.setAttribute("aria-label", button.title);
}
$("#theme").addEventListener("click", () => {
  prefs = { ...prefs, theme: isDark() ? "light" : "dark" };
  savePrefs(prefs);
  applyTheme();
});
systemDark.addEventListener("change", applyTheme);

// --- header: live dot + counts, BETA marker ----------------------------------------------------
const liveText: Record<StreamState, string> = {
  connecting: "connecting…",
  connected: "",
  reconnecting: "reconnecting…",
  down: "offline — retrying",
};
const liveTitle: Record<StreamState, string> = {
  connecting: "Connecting to the live updates",
  connected: "Live: updates arrive as they happen",
  reconnecting: "The live connection dropped; reconnecting",
  down: "No live connection; the list may be out of date",
};

function renderLive(): void {
  const active = store.active;
  const working = active.filter((s) => s.status === "working").length;
  const text = liveText[store.stream] || `${working} working · ${active.length - working} queued`;
  const live = $("#live");
  live.innerHTML = `<span class="dot ${store.stream}"></span>${escapeHtml(text)}`;
  live.title = liveTitle[store.stream];
}
store.addEventListener("change", renderLive);

/** Only beta says that it's beta, with its build version. */
async function loadServerInfo(): Promise<void> {
  try {
    const info = await unwrap(serverInfoApiGet({ client }));
    $("#env").innerHTML =
      info.environment === "beta"
        ? `<span class="env">BETA</span> <span class="version">${escapeHtml(info.version ?? "")}</span>`
        : "";
  } catch {
    $("#env").innerHTML = ""; // cosmetic: never in the way
  }
}

// --- signed in or out ----------------------------------------------------------------------------
function userName(): string {
  const u = auth.getUser();
  return u?.preferred_username ?? u?.name ?? u?.email ?? "";
}

let signedIn = false;
function renderAuthState(): void {
  const now = auth.isAuthenticated();
  $("#signin").hidden = now;
  $("#app").hidden = !now;
  for (const id of ["#live", "#open-options", "#logout"]) $(id).hidden = !now;
  $("#user").textContent = now ? userName() : "";
  if (now === signedIn) return;
  signedIn = now;
  if (now) {
    store.start();
    void loadServerInfo();
    fillComposer();
    route();
  } else {
    store.stop();
    $("#env").innerHTML = "";
  }
}
$("#login").addEventListener("click", () => void auth.login());
$("#logout").addEventListener("click", () => auth.logout());

// --- toast (under the composer) ------------------------------------------------------------------
let toastTimer: ReturnType<typeof setTimeout> | undefined;
function toast(text: string, error = false): void {
  const el = $("#toast");
  el.textContent = text;
  el.classList.toggle("error", error);
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => (el.textContent = ""), error ? 8000 : 3000);
}

// --- composer ------------------------------------------------------------------------------------
const prompt = $<HTMLTextAreaElement>("#prompt");
const createButton = $<HTMLButtonElement>("#create");
const fallbackCaller = () => userName() || "web";

function fillComposer(): void {
  $<HTMLInputElement>("#c-model").value = prefs.defaults.model;
  const caller = $<HTMLInputElement>("#c-caller");
  caller.value = prefs.defaults.caller;
  caller.placeholder = fallbackCaller();
  $<HTMLInputElement>("#c-timeout").value = prefs.defaults.timeoutSeconds?.toString() ?? "";
}

function fitPrompt(): void {
  prompt.style.height = "auto";
  prompt.style.height = `${prompt.scrollHeight + 2}px`;
  prompt.classList.toggle("multiline", prompt.value.includes("\n") || prompt.scrollHeight > 42);
}

$("#advanced").addEventListener("click", () => {
  const open = $(".composer .settings").classList.toggle("open");
  const toggle = $("#advanced");
  toggle.setAttribute("aria-expanded", String(open));
  toggle.textContent = open ? "Advanced ▴" : "Advanced ▾";
});
prompt.addEventListener("input", () => {
  createButton.disabled = prompt.value.trim() === "";
  fitPrompt();
});
prompt.addEventListener("keydown", (e) => {
  if (e.key === "Enter" && (e.ctrlKey || e.metaKey))
    $<HTMLFormElement>("#composer").requestSubmit();
});
$("#composer").addEventListener("submit", (e) => {
  e.preventDefault();
  void create();
});

async function create(): Promise<void> {
  const parsed = newSession(
    {
      prompt: prompt.value,
      model: $<HTMLInputElement>("#c-model").value,
      caller: $<HTMLInputElement>("#c-caller").value,
      timeout: $<HTMLInputElement>("#c-timeout").value,
    },
    fallbackCaller(),
  );
  if (!parsed.ok) {
    toast(parsed.problem, true);
    return;
  }
  createButton.disabled = true;
  try {
    const { id } = await unwrap(sessionsCreate({ client, body: parsed.value }));
    // Create returns only the id; read the session to show it before its events arrive.
    const session = await unwrap(sessionsGet({ client, path: { id } }));
    store.upsert(session);
    prompt.value = "";
    fitPrompt();
    fillComposer();
    setView("overview");
    toast(
      session.status === "queued"
        ? `Queued ${shortId(session)} at position ${session.queuePosition ?? "?"} (all slots busy).`
        : `Started ${shortId(session)}.`,
    );
  } catch (error) {
    toast(describeError(error), true);
  }
  createButton.disabled = prompt.value.trim() === "";
}

// --- views ---------------------------------------------------------------------------------------
function setView(view: View): void {
  list.view = view;
  for (const b of document.querySelectorAll<HTMLElement>("[data-view]")) {
    b.classList.toggle("on", b.dataset.view === view);
  }
}
$("#views").addEventListener("click", (e) => {
  const view = (e.target as HTMLElement).closest<HTMLElement>("[data-view]")?.dataset.view;
  if (view === "overview" || view === "history") setView(view);
});

// --- what the cards ask for ----------------------------------------------------------------------
const idOf = (e: Event) => (e as CustomEvent<string>).detail;
list.addEventListener("open-session", (e) => {
  location.hash = `#/sessions/${idOf(e)}`;
});
list.addEventListener("kill-session", (e) => askKill(idOf(e)));
list.addEventListener("delete-session", (e) => askDelete(idOf(e)));
list.addEventListener("copy-id", (e) => void copy(idOf(e)));
list.addEventListener("toggle-times", () => {
  prefs = { ...prefs, times: prefs.times === "relative" ? "absolute" : "relative" };
  savePrefs(prefs);
  list.times = prefs.times;
});

async function copy(text: string): Promise<void> {
  try {
    await navigator.clipboard.writeText(text);
  } catch {
    // No clipboard API over plain HTTP: the old way.
    const area = Object.assign(document.createElement("textarea"), { value: text });
    document.body.append(area);
    area.select();
    document.execCommand("copy");
    area.remove();
  }
  toast(`Copied ${text}`);
}

/** A dialog's confirm button: runs `action`, closing on success and showing the problem otherwise. */
function confirmWith(
  dialog: HTMLDialogElement,
  problem: HTMLElement,
  action: () => Promise<void>,
): void {
  const form = dialog.querySelector("form");
  if (!form) return;
  form.onsubmit = async (e) => {
    e.preventDefault();
    const confirm = form.querySelector<HTMLButtonElement>('[value="confirm"]');
    if (confirm) confirm.disabled = true;
    try {
      await action();
      dialog.close();
    } catch (error) {
      problem.textContent = describeError(error);
    } finally {
      if (confirm) confirm.disabled = false;
    }
  };
}

function describe(session: Session): string {
  return `${shortId(session)} — “${session.prompt}”.`;
}

function askKill(id: string): void {
  const session = store.get(id);
  if (!session) return;
  const queued = session.status === "queued";
  const dialog = $<HTMLDialogElement>("#kill-dialog");
  const problem = $("#kill-problem");
  const reason = $<HTMLInputElement>("#kill-reason");
  $("#kill-title").textContent = queued ? "Cancel this queued session?" : "Kill this session?";
  $("#kill-what").textContent =
    `${describe(session)} ${queued ? "It never starts." : "Its agent stops now."}`;
  $("#kill-confirm").textContent = queued ? "Cancel session" : "Kill";
  reason.value = "";
  problem.textContent = "";
  confirmWith(dialog, problem, async () => {
    const body = reason.value.trim()
      ? { caller: prefs.defaults.caller || fallbackCaller(), reason: reason.value.trim() }
      : { caller: prefs.defaults.caller || fallbackCaller() };
    store.upsert(await unwrap(sessionsKill({ client, path: { id }, body })));
  });
  dialog.showModal();
}

function askDelete(id: string): void {
  const session = store.get(id);
  if (!session) return;
  const dialog = $<HTMLDialogElement>("#delete-dialog");
  const problem = $("#delete-problem");
  $("#delete-what").textContent =
    `${describe(session)} It's removed from History and search for good.`;
  problem.textContent = "";
  confirmWith(dialog, problem, async () => {
    await unwrap(sessionsDelete({ client, path: { id } }));
    store.remove(id);
  });
  dialog.showModal();
}

// --- options -------------------------------------------------------------------------------------
$("#open-options").addEventListener("click", () => {
  $<HTMLInputElement>("#o-model").value = prefs.defaults.model;
  const caller = $<HTMLInputElement>("#o-caller");
  caller.value = prefs.defaults.caller;
  caller.placeholder = fallbackCaller();
  $<HTMLInputElement>("#o-timeout").value = prefs.defaults.timeoutSeconds?.toString() ?? "";
  $<HTMLSelectElement>("#o-theme").value = prefs.theme ?? "";
  $("#options-problem").textContent = "";
  const dialog = $<HTMLDialogElement>("#options-dialog");
  const form = dialog.querySelector("form");
  if (form) {
    form.onsubmit = (e) => {
      const model = $<HTMLInputElement>("#o-model").value.trim();
      const timeout = parseTimeout($<HTMLInputElement>("#o-timeout").value);
      const problem = !parseModel(model)
        ? "The model is provider/model, e.g. fake/fake."
        : !timeout.ok
          ? timeout.problem
          : "";
      if (problem || !timeout.ok) {
        e.preventDefault();
        $("#options-problem").textContent = problem;
        return;
      }
      const theme = $<HTMLSelectElement>("#o-theme").value as Theme | "";
      prefs = {
        ...prefs,
        defaults: {
          model,
          caller: $<HTMLInputElement>("#o-caller").value.trim(),
          timeoutSeconds: timeout.value,
        },
        theme: theme || undefined,
      };
      savePrefs(prefs);
      applyTheme();
      fillComposer();
    };
  }
  dialog.showModal();
});

// --- the session page: #/sessions/<id> (its own mock comes with M5b) -----------------------------
async function route(): Promise<void> {
  const id = location.hash.match(/^#\/sessions\/(.+)$/)?.[1];
  for (const el of [$("#composer"), $("#views"), list]) el.hidden = !!id;
  $("#detail").hidden = !id;
  if (!id || !signedIn) return;
  const body = $("#detail-body");
  const show = (s: Session) => {
    body.innerHTML = `<strong>${escapeHtml(s.prompt)}</strong><br>${shortId(s)} · ${escapeHtml(s.status)}<br><br>
      The session page — details and the full log — comes with M5b.`;
  };
  const known = store.get(id);
  if (known) show(known);
  else body.textContent = "Loading…";
  scrollTo(0, 0);
  try {
    show(await unwrap(sessionsGet({ client, path: { id } })));
  } catch (error) {
    if (!known) body.textContent = describeError(error);
  }
}
window.addEventListener("hashchange", () => void route());

// Back / Cancel in any dialog.
document.addEventListener("click", (e) => {
  const close = (e.target as HTMLElement).closest?.("[data-close]");
  close?.closest("dialog")?.close();
});

// --- keys ----------------------------------------------------------------------------------------
// The first Tab goes straight to the prompt; Shift+Tab walks back through the header as usual.
document.addEventListener("keydown", (e) => {
  const nothingFocused = document.activeElement === document.body || document.activeElement == null;
  if (
    e.key === "Tab" &&
    !e.shiftKey &&
    nothingFocused &&
    !$("#app").hidden &&
    !$("#composer").hidden
  ) {
    e.preventDefault();
    prompt.focus();
  }
});

// --- start ---------------------------------------------------------------------------------------
applyTheme();
setView("overview");
renderLive();
auth.onChange(renderAuthState);
// Bootstrap OIDC — completes the redirect callback if this load is one, otherwise restores a
// persisted session — then reflect the result.
auth
  .init()
  .then(renderAuthState)
  .catch(() => renderAuthState());
