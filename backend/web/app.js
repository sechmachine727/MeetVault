// Console entry point: session bootstrap, rail, hash routing, view mounting.
import { createApi, ApiError } from "./lib/api.js";
import { createSessionStore } from "./lib/state.js";
import { parseHash, toHash } from "./lib/router.js";
import { el, clear } from "./lib/dom.js";
import { friendlyError } from "./lib/format.js";
import { renderSignIn } from "./views/signin.js";
import { renderMeetings } from "./views/meetings.js";
import { renderMeeting } from "./views/meeting.js";
import { renderSystem } from "./views/system.js";

const store = createSessionStore(window.localStorage);
const api = createApi({ onAuthFailure: () => hardSignOut("session_expired") });
api.setToken(store.getToken());

const toastNode = document.getElementById("toast");
let toastTimer = null;
function toast(message) {
  toastNode.textContent = message;
  toastNode.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => {
    toastNode.hidden = true;
  }, 4200);
}

function navigate(name, options) {
  const next = toHash(name, options);
  if (window.location.hash === next) {
    renderRoute();
    return;
  }
  window.location.hash = next;
}

const ctx = {
  api,
  store,
  navigate,
  toast,
  session: null,
  signOut: () => hardSignOut(null),
  reload: () => renderRoute(),
};

function hardSignOut(reason) {
  store.clear();
  api.setToken(null);
  ctx.session = null;
  if (reason) toast(friendlyError({ code: reason }));
  if (parseHash(window.location.hash).name !== "login") navigate("login");
  else renderRoute();
}

async function signIn(email, password) {
  const result = await api.login(email, password);
  store.save(result.token, result.user);
  api.setToken(result.token);
  ctx.session = { user: result.user, expires_at: result.expires_at };
  return result;
}
ctx.signIn = signIn;

function renderRail() {
  const shell = document.getElementById("app");
  clear(shell);
  const route = parseHash(window.location.hash);
  const user = ctx.session ? ctx.session.user : null;
  const nav = el(
    "nav",
    {},
    el(
      "a",
      {
        href: "#/meetings",
        dataset: { nav: "meetings" },
        "aria-current": route.name === "meeting" || route.name === "meetings" ? "page" : null,
      },
      "Meetings",
    ),
    el(
      "a",
      { href: "#/system", dataset: { nav: "system" }, "aria-current": route.name === "system" ? "page" : null },
      "Service",
    ),
  );
  const footer = el(
    "footer",
    {},
    user
      ? el(
          "div",
          { class: "account" },
          el("span", { class: "who", text: user.display_name || user.email }),
          el("span", { class: "mail", text: user.email }),
          el(
            "button",
            {
              class: "ghost",
              type: "button",
              onclick: async () => {
                try {
                  await api.logout();
                } catch {
                  /* revoking a dead token is not an error */
                }
                hardSignOut(null);
                toast("Signed out.");
              },
            },
            "Sign out",
          ),
        )
      : el("a", { href: "#/login" }, "Sign in"),
  );
  const rail = el(
    "aside",
    { class: "rail" },
    el(
      "div",
      { class: "brand" },
      el("span", { class: "brand-mark", text: "MV" }),
      el("span", { class: "brand-name", text: "MeetVault" }),
      el("span", { class: "brand-sub", text: "Cloud" }),
    ),
    nav,
    footer,
  );
  const main = el("main", { class: "view", id: "view", "aria-live": "polite" });
  shell.append(rail, main);
  return main;
}

async function renderRoute() {
  const route = parseHash(window.location.hash);
  const main = renderRail();
  try {
    if (!ctx.session && store.getToken()) {
      ctx.session = await api.me();
    }
  } catch {
    ctx.session = null;
  }
  try {
    if (route.name === "login" || (!ctx.session && route.name !== "system")) {
      main.append(renderSignIn(ctx));
      return;
    }
    if (route.name === "meeting" && route.id) {
      main.append(await renderMeeting(ctx, route));
      return;
    }
    if (route.name === "system") {
      main.append(await renderSystem(ctx));
      return;
    }
    main.append(await renderMeetings(ctx, route));
  } catch (err) {
    main.append(el("div", { class: "banner error", text: err instanceof ApiError ? friendlyError(err) : String(err) }));
  }
}

window.addEventListener("hashchange", () => {
  renderRoute().catch((err) => {
    const main = document.getElementById("view");
    if (main) {
      main.append(el("div", { class: "banner error", text: err instanceof ApiError ? friendlyError(err) : String(err) }));
    }
  });
});

renderRoute().catch((err) => {
  const shell = document.getElementById("app");
  if (shell) shell.append(el("div", { class: "banner error", text: String(err) }));
});
