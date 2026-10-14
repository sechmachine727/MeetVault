// Pure presentation logic for the MeetVault console. No DOM, no fetch — unit-tested by
// `node --test tests/`. Keep it that way: anything that needs a browser belongs in a view.

const MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

export function formatDuration(ms) {
  if (ms === null || ms === undefined || Number.isNaN(ms)) return "—";
  const total = Math.max(0, Math.round(ms / 1000));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  const mm = h > 0 ? String(m).padStart(2, "0") : String(m);
  return `${h > 0 ? `${h}:` : ""}${mm}:${String(s).padStart(2, "0")}`;
}

export function formatElapsed(startedAtIso, nowMs) {
  if (!startedAtIso) return "";
  const started = Date.parse(parseServerTime(startedAtIso));
  if (Number.isNaN(started)) return "";
  const secs = Math.max(0, Math.floor(((nowMs ?? Date.now()) - started) / 1000));
  if (secs < 60) return `${secs}s`;
  const m = Math.floor(secs / 60);
  const s = secs % 60;
  if (m < 60) return `${m}m ${s}s`;
  return `${Math.floor(m / 60)}h ${m % 60}m`;
}

// The backend stores UTC but SQLite hands the timestamps back NAIVE, so /status can emit
// "2026-10-15T00:31:00" with no zone designator. Date.parse() would read that as LOCAL time
// (a 7-hour error on this box, and a different one everywhere else), so a missing offset is
// pinned to UTC before parsing.
export function parseServerTime(value) {
  if (typeof value !== "string" || value === "") return value;
  const hasZone = /(Z|[+-]\d{2}:?\d{2})$/.test(value);
  return hasZone ? value : `${value}Z`;
}

export function formatDate(iso) {
  if (!iso) return "—";
  const d = new Date(parseServerTime(iso));
  if (Number.isNaN(d.getTime())) return "—";
  const pad = (n) => String(n).padStart(2, "0");
  return `${d.getUTCDate()} ${MONTHS[d.getUTCMonth()]} ${d.getUTCFullYear()}, ${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}`;
}

export function formatBytes(bytes) {
  if (!bytes) return "0 B";
  const units = ["B", "KB", "MB", "GB", "TB"];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit += 1;
  }
  const shown = unit === 0 ? String(Math.round(value)) : value.toFixed(1);
  return `${shown} ${units[unit]}`;
}

// Server vocabulary (docs/API.md): status = uploaded|processing|ready|failed|deleted;
// latest_job_state = queued|running|succeeded|failed|cancelled.
export function statusKind(meeting) {
  if (!meeting) return "unprocessed";
  if (meeting.status === "deleted") return "deleted";
  if (meeting.status === "failed") return "failed";
  if (meeting.status === "ready") return "ready";
  if (meeting.status === "processing") return "processing";
  const job = meeting.latest_job_state;
  if (job === "running") return "processing";
  if (job === "queued") return "queued";
  if (job === "failed") return "failed";
  return "unprocessed";
}

export function statusLabel(kind) {
  return (
    {
      ready: "Ready",
      processing: "Processing",
      queued: "Queued",
      failed: "Failed",
      unprocessed: "Not processed",
      deleted: "Deleted",
    }[kind] ?? kind
  );
}

export function isProcessing(state) {
  return state === "queued" || state === "running";
}

export function describeJobState(status) {
  const steps = (status && status.steps) || {};
  const running = Object.entries(steps).find(([, state]) => state === "running");
  if (running) return `${running[0]} running`;
  const queued = Object.entries(steps).find(([, state]) => state === "queued");
  if (queued) return `${queued[0]} queued`;
  return "";
}

const ERROR_TEXT = {
  not_found: "That meeting is not on the server.",
  session_expired: "Your session expired. Sign in again.",
  unauthorized: "Sign in to use the cloud.",
  invalid_credentials: "That email and password do not match.",
  no_analysis: "There is no current analysis to export yet.",
  network_error: "The service is unreachable.",
  payload_too_large: "That file is too large for the server.",
  validation_error: "The server rejected that request.",
};

export function friendlyError(errorBody) {
  const code = errorBody && errorBody.code ? errorBody.code : "error";
  // An unmapped code falls back to the server's own sentence (e.g. 409 `conflict`: "Cannot delete
  // the active transcript…"), then to the bare code so nothing is ever swallowed silently.
  return ERROR_TEXT[code] ?? (errorBody && errorBody.message) ?? code;
}

// Genuinely out-of-date analysis kinds: stale with NO fresh document of that kind. A re-run marks
// its predecessor stale while the new one is current, so an unfiltered stale_kinds list cries
// stale over current data (the desktop client filters the same way).
export function outOfDateKinds(status) {
  const stale = (status && status.stale_kinds) || [];
  const fresh = new Set((status && status.fresh_kinds) || []);
  return stale.filter((kind) => !fresh.has(kind));
}

// The analysis bundle copies status' stale_kinds but carries no fresh_kinds, so the same rule is
// applied against the bundle's own contents: a kind is out of date only when it is stale AND the
// bundle has no document of that kind.
export function outOfDateBundleKinds(bundle) {
  const stale = (bundle && bundle.stale_kinds) || [];
  const present = new Set();
  if (bundle) {
    if (bundle.summary) present.add("summary");
    if (bundle.agenda) present.add("agenda");
    if (bundle.decisions) present.add("decisions");
    if (bundle.action_items) present.add("action_items");
    if ((bundle.sections || []).length) present.add("section_summary");
    if ((bundle.qa || []).length) present.add("qa");
  }
  return stale.filter((kind) => !present.has(kind));
}

export function pagingRange({ total, limit, offset }) {
  if (!total) return { from: 0, to: 0, total: 0, hasPrev: false, hasNext: false };
  const from = offset + 1;
  const to = Math.min(offset + limit, total);
  return { from, to, total, hasPrev: offset > 0, hasNext: to < total };
}
