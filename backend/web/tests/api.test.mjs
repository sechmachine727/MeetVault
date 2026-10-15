import test from "node:test";
import assert from "node:assert/strict";

import { ApiError, createApi } from "../lib/api.js";

function jsonResponse(status, body) {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: async () => body,
    text: async () => JSON.stringify(body),
    headers: new Headers({ "content-type": "application/json" }),
  };
}

test("a request carries the bearer token and parses JSON", async () => {
  const calls = [];
  const api = createApi({
    fetchImpl: async (url, opts) => {
      calls.push({ url, opts });
      return jsonResponse(200, { items: [], total: 0, limit: 20, offset: 0 });
    },
    onAuthFailure: () => {},
  });
  api.setToken("tok-9");
  const page = await api.listMeetings({ q: "sync" });
  assert.equal(calls[0].url, "/v1/meetings?q=sync");
  assert.equal(calls[0].opts.headers.Authorization, "Bearer tok-9");
  assert.equal(page.total, 0);
});

test("the error envelope becomes an ApiError with its code", async () => {
  const api = createApi({
    fetchImpl: async () =>
      jsonResponse(404, { error: { code: "not_found", message: "Meeting not found" }, request_id: "r1" }),
    onAuthFailure: () => {},
  });
  await assert.rejects(
    () => api.getMeeting("gone"),
    (err) => {
      assert.ok(err instanceof ApiError);
      assert.equal(err.status, 404);
      assert.equal(err.code, "not_found");
      assert.equal(err.requestId, "r1");
      return true;
    },
  );
});

test("a 401 fires the auth-failure hook", async () => {
  let fired = 0;
  const api = createApi({
    fetchImpl: async () =>
      jsonResponse(401, { error: { code: "session_expired", message: "expired" }, request_id: "r2" }),
    onAuthFailure: () => {
      fired += 1;
    },
  });
  await assert.rejects(() => api.me());
  assert.equal(fired, 1);
});

test("a 401 from the LOGIN endpoint is bad credentials, not a dead session", async () => {
  // Firing the hook here re-renders the page and wipes the "wrong password" message — the
  // sign-in screen then shows a misleading "session expired" toast instead.
  let fired = 0;
  const api = createApi({
    fetchImpl: async () =>
      jsonResponse(401, { error: { code: "invalid_credentials", message: "Bad email or password" }, request_id: "r3" }),
    onAuthFailure: () => {
      fired += 1;
    },
  });
  await assert.rejects(
    () => api.login("demo@meetvault.local", "wrong"),
    (err) => {
      assert.equal(err.code, "invalid_credentials");
      return true;
    },
  );
  assert.equal(fired, 0);
});

test("a transport failure becomes a network_error ApiError, not a raw TypeError", async () => {
  const api = createApi({
    fetchImpl: async () => {
      throw new TypeError("Failed to fetch");
    },
    onAuthFailure: () => {},
  });
  await assert.rejects(
    () => api.me(),
    (err) => {
      assert.equal(err.code, "network_error");
      assert.equal(err.status, 0);
      return true;
    },
  );
});

test("a 202 process response and an OkResponse DELETE both parse", async () => {
  const bodies = [
    jsonResponse(202, { meeting_id: "m1", steps: ["analyze"], job_ids: ["j1"], enqueued: 1 }),
    jsonResponse(200, { ok: true, detail: {} }),
  ];
  const api = createApi({ fetchImpl: async () => bodies.shift(), onAuthFailure: () => {} });
  const processed = await api.process("m1", { steps: ["analyze"], force: true });
  assert.equal(processed.enqueued, 1);
  const removed = await api.deleteMeeting("m1");
  assert.equal(removed.ok, true);
});

test("process() sends steps as repeated query parameters, never a comma list", async () => {
  const calls = [];
  const api = createApi({
    fetchImpl: async (url, opts) => {
      calls.push({ url, opts });
      return jsonResponse(202, { meeting_id: "m1", steps: [], job_ids: [], enqueued: 1 });
    },
    onAuthFailure: () => {},
  });
  await api.process("m1", { steps: ["transcribe", "analyze"], force: true });
  assert.equal(calls[0].url, "/v1/meetings/m1/process?steps=transcribe&steps=analyze&force=true");
});

test("the speaker merge body uses the server's field name", async () => {
  const calls = [];
  const api = createApi({
    fetchImpl: async (url, opts) => {
      calls.push({ url, opts });
      return jsonResponse(200, { merged_speaker_id: "a", target_speaker_id: "b", moved_segments: 0 });
    },
    onAuthFailure: () => {},
  });
  await api.mergeSpeaker("a", "b");
  assert.equal(calls[0].url, "/v1/speakers/a/merge");
  assert.equal(JSON.parse(calls[0].opts.body).target_speaker_id, "b");
});

test("register posts the account body to /v1/auth/register", async () => {
  const calls = [];
  const api = createApi({
    fetchImpl: async (url, opts) => {
      calls.push({ url, body: JSON.parse(opts.body) });
      return jsonResponse(201, {
        token: "t",
        expires_at: "2026-01-01T00:00:00Z",
        user: { email: "a@b.com" },
      });
    },
    onAuthFailure: () => {},
  });
  const out = await api.register("a@b.com", "long-enough-1", "A");
  assert.equal(calls[0].url, "/v1/auth/register");
  assert.deepEqual(calls[0].body, { email: "a@b.com", password: "long-enough-1", display_name: "A" });
  assert.equal(out.token, "t");
});

test("register sends an empty display_name, never undefined", async () => {
  const calls = [];
  const api = createApi({
    fetchImpl: async (url, opts) => {
      calls.push({ url, body: JSON.parse(opts.body) });
      return jsonResponse(201, { token: "t", expires_at: "2026-01-01T00:00:00Z", user: { email: "a@b.com" } });
    },
    onAuthFailure: () => {},
  });
  await api.register("a@b.com", "long-enough-1");
  assert.equal(calls[0].body.display_name, "");
});

test("a 401 from REGISTER is not a dead session either", async () => {
  let fired = 0;
  const api = createApi({
    fetchImpl: async () =>
      jsonResponse(401, { error: { code: "email_taken", message: "taken" }, request_id: "r9" }),
    onAuthFailure: () => {
      fired += 1;
    },
  });
  await assert.rejects(() => api.register("a@b.com", "long-enough-1", ""));
  assert.equal(fired, 0);
});
