import { el, clear } from "../lib/dom.js";
import {
  formatDate,
  formatDuration,
  formatElapsed,
  statusKind,
  statusLabel,
  describeJobState,
  friendlyError,
  pagingRange,
  isProcessing,
} from "../lib/format.js";
import { shouldPoll, POLL_MS } from "../lib/state.js";

const PAGE_SIZE = 20;

export async function renderMeetings(ctx, route) {
  const state = {
    q: route.params.q || "",
    status: route.params.status || "",
    offset: Number(route.params.offset || 0),
    page: null,
    statusById: {},
    pollTimer: null,
    searchTimer: null,
    disposed: false,
  };

  const rows = el("div", { class: "rows" });
  const summary = el("p", { class: "sub", "data-testid": "page-summary" });
  const listCard = el("div", { class: "card" }, rows, summary);
  const staleBanner = el("div", { class: "banner", hidden: true, "data-testid": "stale-banner" });

  const search = el("input", {
    type: "search",
    placeholder: "Search titles…",
    value: state.q,
    "aria-label": "Search meetings",
    // `input` (debounced), not `change`: a change event only fires on blur, so typing then
    // reading the list would show stale rows.
    oninput: (event) => {
      state.q = event.target.value.trim();
      clearTimeout(state.searchTimer);
      state.searchTimer = setTimeout(() => {
        state.offset = 0;
        ctx.navigate("meetings", { params: params() });
      }, 250);
    },
  });
  const statusFilter = el(
    "select",
    {
      "aria-label": "Filter by status",
      onchange: (event) => {
        state.status = event.target.value;
        state.offset = 0;
        ctx.navigate("meetings", { params: params() });
      },
    },
    ...[
      ["", "All statuses"],
      ["ready", "Ready"],
      ["processing", "Processing"],
      ["uploaded", "Uploaded"],
      ["failed", "Failed"],
    ].map(([value, label]) => el("option", { value, selected: state.status === value }, label)),
  );
  const refresh = el("button", { type: "button", onclick: () => load(true) }, "Refresh");
  const prev = el("button", { type: "button", dataset: { action: "prev" }, onclick: () => goTo(-PAGE_SIZE) }, "Previous");
  const next = el("button", { type: "button", dataset: { action: "next" }, onclick: () => goTo(PAGE_SIZE) }, "Next");
  const toolbar = el("div", { class: "toolbar" }, search, statusFilter, refresh, prev, next);

  function params() {
    const out = {};
    if (state.q) out.q = state.q;
    if (state.status) out.status = state.status;
    if (state.offset) out.offset = String(state.offset);
    return out;
  }

  function goTo(delta) {
    const range = state.page ? pagingRange(state.page) : null;
    if (delta < 0 && range && range.hasPrev) state.offset = Math.max(0, state.offset - PAGE_SIZE);
    else if (delta > 0 && range && range.hasNext) state.offset += PAGE_SIZE;
    else return;
    ctx.navigate("meetings", { params: params() });
  }

  function renderRow(meeting) {
    const kind = statusKind(meeting);
    const status = state.statusById[meeting.id] || null;
    const elapsed =
      status && status.active_job_started_at ? formatElapsed(status.active_job_started_at, Date.now()) : "";
    const step = status ? describeJobState(status) : "";
    const progressText =
      status && typeof status.progress === "number" && isProcessing(meeting.latest_job_state)
        ? `${Math.round((status.progress || 0) * 100)}%`
        : "";
    const statusBits = [statusLabel(kind), step, progressText, elapsed].filter(Boolean);
    const badges = [];
    if (meeting.has_analysis) badges.push(el("span", { class: "pill", "data-testid": "analyzed-badge", text: "Analyzed" }));
    // Deliberately NO mock badge here: the list payload cannot prove what was mocked, and the
    // aggregate /status flag is true whenever ANY step ran mocked (it told the owner a genuine
    // analysis was mock data — see the Details badge pitfall in the project notes). The badges
    // that state WHAT is mock live on the detail header ("Mock transcript") and the Analysis tab
    // ("Mock analysis"), each derived from the object it describes.
    if (status && status.stale_kinds && status.stale_kinds.length) {
      badges.push(
        el("span", {
          class: "pill stale",
          "data-testid": "stale-pill",
          text: `Outdated: ${status.stale_kinds.join(", ")}`,
        }),
      );
    }
    return el(
      "div",
      { class: "row", dataset: { row: meeting.id, kind } },
      el(
        "div",
        {},
        el("div", { class: "title", text: meeting.title }),
        el("div", {
          class: "meta",
          text: `${formatDate(meeting.created_at)} · ${formatDuration(meeting.duration_ms)} · ${meeting.language || "auto"}`,
        }),
        el(
          "div",
          { class: "status" },
          el("span", { class: "pill", dataset: { kind }, "data-testid": "status", text: statusBits.join(" · ") }),
          ...badges,
        ),
      ),
      el(
        "div",
        { class: "actions" },
        el(
          "button",
          { class: "primary", type: "button", dataset: { action: "open", id: meeting.id }, onclick: () => ctx.navigate("meeting", { id: meeting.id }) },
          "Open",
        ),
        el(
          "button",
          { class: "danger", type: "button", dataset: { action: "remove", id: meeting.id }, onclick: () => confirmRemove(meeting) },
          "Remove from cloud",
        ),
      ),
    );
  }

  function confirmRemove(meeting) {
    const dialog = el(
      "dialog",
      { "data-testid": "remove-dialog" },
      el("h3", {}, "Remove from cloud?"),
      el(
        "p",
        {},
        `“${meeting.title}” will be deleted from the server, including its recordings on the server. The local recording on this machine is not touched.`,
      ),
      el(
        "div",
        { class: "dialog-actions" },
        el("button", { type: "button", class: "cancel", onclick: () => dialog.close() }, "Cancel"),
        el(
          "button",
          {
            type: "button",
            class: "primary confirm",
            onclick: async () => {
              try {
                await ctx.api.deleteMeeting(meeting.id);
                dialog.close();
                ctx.toast("Removed from cloud.");
                await load(true);
              } catch (err) {
                dialog.close();
                ctx.toast(friendlyError(err));
              }
            },
          },
          "Remove",
        ),
      ),
    );
    document.body.append(dialog);
    dialog.addEventListener("close", () => dialog.remove());
    dialog.showModal();
  }

  function schedulePoll() {
    clearTimeout(state.pollTimer);
    if (state.disposed) return;
    const anyProcessing = shouldPoll(state.page ? state.page.items : []);
    if (!anyProcessing) return;
    state.pollTimer = setTimeout(() => load(false), POLL_MS);
  }

  async function load(showSpinner) {
    if (state.disposed) return;
    if (showSpinner) refresh.disabled = true;
    try {
      const page = await ctx.api.listMeetings({
        q: state.q,
        status: state.status,
        limit: PAGE_SIZE,
        offset: state.offset,
      });
      state.page = page;
      // Elapsed time, progress and staleness live on /status, not on the list payload: fetch it
      // for the rows that are moving or already analyzed (bounded to one page).
      const interesting = page.items.filter((m) => isProcessing(m.latest_job_state) || m.has_analysis);
      const statuses = await Promise.all(
        interesting.map((m) =>
          ctx.api.status(m.id).then(
            (value) => [m.id, value],
            () => [m.id, null],
          ),
        ),
      );
      state.statusById = Object.fromEntries(statuses);
      clear(rows);
      const stale = Object.values(state.statusById)
        .flatMap((s) => (s ? s.stale_kinds : []))
        .filter(Boolean);
      if (stale.length) {
        staleBanner.textContent = `Some analysis is out of date for the material that is now active: ${[...new Set(stale)].join(", ")}. Re-run analysis to refresh it.`;
        staleBanner.hidden = false;
      } else {
        staleBanner.hidden = true;
      }
      if (!page.items.length) {
        rows.append(
          el("div", {
            class: "empty",
            "data-testid": "empty",
            text:
              state.q || state.status
                ? "No meetings match that filter."
                : "No meetings on the server yet. Send one from the desktop app.",
          }),
        );
      } else {
        for (const meeting of page.items) rows.append(renderRow(meeting));
      }
      const range = pagingRange(page);
      summary.textContent = `${range.from}–${range.to} of ${range.total}`;
      prev.disabled = !range.hasPrev;
      next.disabled = !range.hasNext;
    } catch (err) {
      rows.append(el("div", { class: "banner error", text: friendlyError(err) }));
    } finally {
      refresh.disabled = false;
      schedulePoll();
    }
  }

  const view = el(
    "div",
    {},
    el("h1", {}, "Meetings in the cloud"),
    el(
      "p",
      { class: "sub" },
      "The server is the only source of truth here: meetings recorded locally but never uploaded do not appear.",
    ),
    toolbar,
    staleBanner,
    listCard,
  );

  // Stop polling when the view is replaced.
  window.addEventListener(
    "hashchange",
    () => {
      state.disposed = true;
      clearTimeout(state.pollTimer);
    },
    { once: true },
  );

  await load(true);
  return view;
}
