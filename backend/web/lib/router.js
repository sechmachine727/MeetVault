// Hash routing keeps the SPA server-free: no catch-all route, so /v1, /docs and /openapi.json
// stay exactly as they were. Pure functions -> unit-tested.

const TAB_ROUTES = new Set(["meetings", "login", "system"]);

export function parseHash(hash) {
  const raw = (hash || "").replace(/^#/, "");
  const [pathPart, queryPart] = raw.split("?");
  const segments = pathPart.split("/").filter(Boolean);
  const params = Object.fromEntries(new URLSearchParams(queryPart || ""));
  if (segments.length === 0) return { name: "meetings", id: null, params };
  if (segments[0] === "meetings" && segments[1]) {
    return { name: "meeting", id: decodeURIComponent(segments[1]), params };
  }
  if (TAB_ROUTES.has(segments[0])) return { name: segments[0], id: null, params };
  return { name: "meetings", id: null, params };
}

export function toHash(name, { id, params } = {}) {
  const query = params && Object.keys(params).length ? `?${new URLSearchParams(params)}` : "";
  if (name === "meeting" && id) return `#/meetings/${encodeURIComponent(id)}${query}`;
  return `#/${name}${query}`;
}
