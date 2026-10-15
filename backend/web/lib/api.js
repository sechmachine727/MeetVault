// The MeetVault backend client for the browser. One place that knows the contract in
// backend/docs/API.md: paths, the error envelope, the bearer header.

export class ApiError extends Error {
  constructor(status, envelope) {
    const body = (envelope && envelope.error) || { code: "error", message: "Request failed" };
    super(body.message || body.code);
    this.name = "ApiError";
    this.status = status;
    this.code = body.code;
    this.detail = body.detail || {};
    this.requestId = (envelope && envelope.request_id) || null;
  }
}

export function createApi({ fetchImpl, onAuthFailure, baseUrl = "" } = {}) {
  const doFetch = fetchImpl || globalThis.fetch.bind(globalThis);
  let token = null;
  const fail = onAuthFailure || (() => {});

  async function request(method, path, { body, raw = false, accept } = {}) {
    const headers = {};
    if (token) headers.Authorization = `Bearer ${token}`;
    if (body !== undefined) headers["Content-Type"] = "application/json";
    if (accept) headers.Accept = accept;
    let res;
    try {
      res = await doFetch(baseUrl + path, {
        method,
        headers,
        body: body === undefined ? undefined : JSON.stringify(body),
      });
    } catch {
      throw new ApiError(0, { error: { code: "network_error", message: "The service is unreachable." } });
    }
    // A 401 from an auth ATTEMPT is bad credentials, not a dead session — firing the hook there
    // wiped the form (and the wrong-password message) with "Your session expired".
    const isAuthAttempt = path === "/v1/auth/login" || path === "/v1/auth/register";
    if (res.status === 401 && !isAuthAttempt) fail();
    if (!res.ok) {
      let envelope = null;
      try {
        envelope = await res.json();
      } catch {
        envelope = null;
      }
      throw new ApiError(res.status, envelope);
    }
    if (raw) return res;
    if (res.status === 204) return null;
    const text = typeof res.text === "function" ? await res.text() : "";
    return text ? JSON.parse(text) : null;
  }

  // Arrays become REPEATED parameters (?steps=a&steps=b). A comma-joined single value is one
  // unknown step to the server and answers 422.
  const qs = (params) => {
    const search = new URLSearchParams();
    for (const [key, value] of Object.entries(params || {})) {
      if (value === undefined || value === null || value === "") continue;
      if (Array.isArray(value)) {
        for (const item of value) search.append(key, String(item));
        continue;
      }
      search.set(key, String(value));
    }
    const str = search.toString();
    return str ? `?${str}` : "";
  };

  return {
    setToken(value) {
      token = value || null;
    },
    getToken() {
      return token;
    },
    // Auth
    login: (email, password) => request("POST", "/v1/auth/login", { body: { email, password } }),
    register: (email, password, displayName) =>
      request("POST", "/v1/auth/register", {
        body: { email, password, display_name: displayName || "" },
      }),
    logout: () => request("POST", "/v1/auth/logout"),
    me: () => request("GET", "/v1/auth/me"),
    // Meetings
    listMeetings: (params) => request("GET", `/v1/meetings${qs(params)}`),
    getMeeting: (id) => request("GET", `/v1/meetings/${id}`),
    deleteMeeting: (id) => request("DELETE", `/v1/meetings/${id}`),
    status: (id) => request("GET", `/v1/meetings/${id}/status`),
    process: (id, params) => request("POST", `/v1/meetings/${id}/process${qs(params)}`),
    // Media
    listMedia: (id, params) => request("GET", `/v1/meetings/${id}/media${qs(params)}`),
    mediaInfo: (assetId) => request("GET", `/v1/media/${assetId}/info`),
    deleteMedia: (assetId) => request("DELETE", `/v1/media/${assetId}`),
    async mediaBlob(assetId) {
      const res = await request("GET", `/v1/media/${assetId}`, { raw: true });
      return res.blob();
    },
    // Transcripts
    transcript: (id) => request("GET", `/v1/meetings/${id}/transcript`),
    transcriptText: (id) => request("GET", `/v1/meetings/${id}/transcript/text`, { accept: "text/plain" }),
    transcripts: (id, params) => request("GET", `/v1/meetings/${id}/transcripts${qs(params)}`),
    activateTranscript: (tid) => request("POST", `/v1/transcripts/${tid}/activate`),
    deleteTranscript: (tid) => request("DELETE", `/v1/transcripts/${tid}`),
    // Speakers
    speakers: (id) => request("GET", `/v1/meetings/${id}/speakers`),
    renameSpeaker: (sid, label) => request("PATCH", `/v1/speakers/${sid}`, { body: { label } }),
    mergeSpeaker: (sid, targetId) =>
      request("POST", `/v1/speakers/${sid}/merge`, { body: { target_speaker_id: targetId } }),
    deleteSpeaker: (sid) => request("DELETE", `/v1/speakers/${sid}`),
    // Analysis
    analysis: (id) => request("GET", `/v1/meetings/${id}/analysis`),
    qa: (id) => request("GET", `/v1/meetings/${id}/qa`),
    ask: (id, question, sectionId) =>
      request("POST", `/v1/meetings/${id}/qa`, { body: { question, top_k: 8, section_id: sectionId ?? null } }),
    analysisRuns: (id) => request("GET", `/v1/meetings/${id}/analysis/runs`),
    // Jobs
    jobs: (id) => request("GET", `/v1/meetings/${id}/jobs`),
    job: (jobId) => request("GET", `/v1/jobs/${jobId}`),
    cancelJob: (jobId) => request("POST", `/v1/jobs/${jobId}/cancel`),
    // Export + system
    exportUrl: (id) => `/v1/meetings/${id}/export.docx`,
    async exportDocx(id) {
      const res = await request("GET", `/v1/meetings/${id}/export.docx`, { raw: true });
      return res.blob();
    },
    systemInfo: () => request("GET", "/v1/system/info"),
    providers: () => request("GET", "/v1/system/providers"),
  };
}
