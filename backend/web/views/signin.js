import { el } from "../lib/dom.js";
import { friendlyError } from "../lib/format.js";

export function renderSignIn(ctx) {
  const error = el("div", { class: "banner error", hidden: true, "data-testid": "signin-error" });
  const email = el("input", { type: "email", id: "email", autocomplete: "username", required: true });
  const password = el("input", { type: "password", id: "password", autocomplete: "current-password", required: true });
  const submit = el("button", { class: "primary", type: "submit" }, "Sign in");
  const form = el(
    "form",
    {
      onsubmit: async (event) => {
        event.preventDefault();
        error.hidden = true;
        submit.disabled = true;
        submit.setAttribute("aria-busy", "true");
        try {
          await ctx.signIn(email.value.trim(), password.value);
          ctx.toast("Signed in.");
          ctx.navigate("meetings");
        } catch (err) {
          error.textContent = friendlyError({ code: err && err.code ? err.code : "error" });
          error.hidden = false;
        } finally {
          submit.disabled = false;
          submit.removeAttribute("aria-busy");
        }
      },
    },
    el("h2", {}, "Sign in to the cloud"),
    el(
      "p",
      { class: "sub" },
      "Your recordings and analysis live on this server. The desktop app uses the same account.",
    ),
    error,
    el("label", { class: "field" }, "Email", email),
    el("label", { class: "field" }, "Password", password),
    submit,
    el("p", { class: "sub" }, el("a", { href: "#/signup" }, "Create an account")),
  );
  return el("div", { class: "signin" }, el("div", { class: "card" }, form));
}
