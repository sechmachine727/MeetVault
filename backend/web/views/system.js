import { el } from "../lib/dom.js";
import { friendlyError } from "../lib/format.js";

// `/v1/system/providers` is a dict keyed by capability (transcriber/diarizer/analyzer), each value
// {requested, effective, is_mock, model, reason}. `/v1/system/info` is a flat dict.
export async function renderSystem(ctx) {
  const view = el("div", {}, el("h1", {}, "Service"), el("p", { class: "sub" }, "Which engines this server is actually running."));
  try {
    const [info, providers] = await Promise.all([ctx.api.systemInfo(), ctx.api.providers()]);
    const facts = el("dl", { class: "facts", "data-testid": "system-facts" });
    for (const [key, value] of Object.entries(info || {})) {
      facts.append(el("dt", { text: key }), el("dd", { text: typeof value === "object" ? JSON.stringify(value) : String(value) }));
    }
    const providerRows = el("div", { class: "rows", "data-testid": "providers" });
    for (const [capability, provider] of Object.entries(providers || {})) {
      providerRows.append(
        el(
          "div",
          { class: "row", dataset: { provider: capability } },
          el(
            "div",
            {},
            el("div", { class: "title", text: `${capability} — ${provider.effective || "?"}` }),
            el("div", { class: "meta", text: [provider.model, provider.reason].filter(Boolean).join(" · ") }),
          ),
          provider.is_mock
            ? el("span", { class: "pill mock", "data-testid": "mock-provider", text: "Mock" })
            : el("span", { class: "pill", dataset: { kind: "ready" }, text: "Live" }),
        ),
      );
    }
    view.append(el("div", { class: "card" }, el("h2", {}, "Server"), facts), el("div", { class: "card" }, el("h2", {}, "Providers"), providerRows));
  } catch (err) {
    view.append(el("div", { class: "banner error", text: friendlyError(err) }));
  }
  return view;
}
