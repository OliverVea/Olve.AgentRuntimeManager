import { createApiClient } from "./api-client.js";
import * as auth from "./auth/oidc.js";
import { escapeHtml } from "./base-element.js";
import { SessionList } from "./components/session-list.js";

// In dev, same-origin → Vite proxies /api to the backend (see vite.config.ts). In a build, set
// VITE_API_BASE_URL to point at the API host (defaults to same-origin, which is how we deploy).
const baseUrl = import.meta.env.VITE_API_BASE_URL ?? window.location.origin;

// One client for the whole app. getAccessToken() returns a fresh bearer when logged in (null when
// logged out); onUnauthorized refreshes + retries on a 401.
const client = createApiClient(baseUrl, {
  getToken: auth.getAccessToken,
  onUnauthorized: auth.refresh,
});

customElements.define(SessionList.tagName, SessionList);
const list = document.querySelector<SessionList>(SessionList.tagName);
if (list) {
  list.client = client;
  list.addEventListener("sign-in", () => void auth.login());
}

// --- auth bar: log in / who's-logged-in + log out ---
const bar = document.querySelector<HTMLElement>("#auth");

function renderAuthBar(): void {
  if (!bar) return;
  if (auth.isAuthenticated()) {
    const u = auth.getUser();
    const who = u?.name ?? u?.preferred_username ?? u?.email ?? "Signed in";
    bar.innerHTML = `<span class="who">${escapeHtml(who)}</span><button id="logout">Log out</button>`;
    bar.querySelector<HTMLButtonElement>("#logout")?.addEventListener("click", () => auth.logout());
  } else {
    bar.innerHTML = `<button id="login">Log in</button>`;
    bar
      .querySelector<HTMLButtonElement>("#login")
      ?.addEventListener("click", () => void auth.login());
  }
}

// Every session endpoint needs a token, so the list only loads (and subscribes) while signed in.
function renderAuthState(): void {
  renderAuthBar();
  if (list) list.signedIn = auth.isAuthenticated();
}

auth.onChange(renderAuthState);

// Bootstrap OIDC — completes the redirect callback if this load is one, otherwise restores a
// persisted session — then reflect the result.
auth
  .init()
  .then(renderAuthState)
  .catch(() => renderAuthState());
