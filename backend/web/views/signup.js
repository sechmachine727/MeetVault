import { el } from "../lib/dom.js";
import { friendlyError } from "../lib/format.js";

// Sign-up screen. The fields carry data-testids because Playwright's getByLabel("Password") also
// matches "Confirm password" (label matching is substring + case-insensitive), and picking the
// wrong box would silently test the wrong thing.
export function renderSignUp(ctx) {
  const error = el("div", { class: "banner error", hidden: true, "data-testid": "signup-error" });
  const name = el("input", {
    type: "text",
    id: "display-name",
    autocomplete: "name",
    "data-testid": "signup-name",
  });
  const email = el("input", {
    type: "email",
    id: "email",
    autocomplete: "username",
    required: true,
    "data-testid": "signup-email",
  });
  const password = el("input", {
    type: "password",
    id: "password",
    autocomplete: "new-password",
    required: true,
    "data-testid": "signup-password",
  });
  const confirm = el("input", {
    type: "password",
    id: "confirm",
    autocomplete: "new-password",
    required: true,
    "data-testid": "signup-confirm",
  });
  const submit = el("button", { class: "primary", type: "submit", "data-testid": "signup-submit" }, "Create account");

  const showError = (text) => {
    error.textContent = text;
    error.hidden = false;
  };

  const form = el(
    "form",
    {
      onsubmit: async (event) => {
        event.preventDefault();
        error.hidden = true;
        // Mirror the server's rule so the user gets a sentence they can act on instead of the
        // opaque 422 "The server rejected that request." The server still enforces it.
        if (password.value.length < 8) {
          showError("Password must be at least 8 characters.");
          return;
        }
        if (password.value !== confirm.value) {
          showError("Those passwords do not match.");
          return;
        }
        submit.disabled = true;
        submit.setAttribute("aria-busy", "true");
        try {
          await ctx.signUp(email.value.trim(), password.value, name.value.trim());
          ctx.toast("Account created.");
          ctx.navigate("meetings");
        } catch (err) {
          showError(friendlyError({ code: err && err.code ? err.code : "error", message: err && err.message }));
        } finally {
          submit.disabled = false;
          submit.removeAttribute("aria-busy");
        }
      },
    },
    el("h2", {}, "Create your account"),
    el("p", { class: "sub" }, "Sign up with your email — the desktop app uses the same account."),
    error,
    el("label", { class: "field" }, "Name (optional)", name),
    el("label", { class: "field" }, "Email", email),
    el("label", { class: "field" }, "Password", password),
    el("label", { class: "field" }, "Confirm password", confirm),
    submit,
    el("p", { class: "sub" }, el("a", { href: "#/login" }, "Already have an account? Sign in")),
  );
  return el("div", { class: "signin" }, el("div", { class: "card" }, form));
}
