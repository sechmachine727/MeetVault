import test from "node:test";
import assert from "node:assert/strict";

import {
  formatDuration,
  formatElapsed,
  formatDate,
  formatBytes,
  statusKind,
  statusLabel,
  isProcessing,
  describeJobState,
  friendlyError,
  pagingRange,
} from "../lib/format.js";

test("formatDuration renders h:mm:ss above an hour and m:ss below", () => {
  assert.equal(formatDuration(0), "0:00");
  assert.equal(formatDuration(7000), "0:07");
  assert.equal(formatDuration(83_000), "1:23");
  assert.equal(formatDuration(3_723_000), "1:02:03");
  assert.equal(formatDuration(null), "—");
});

test("formatElapsed counts up from an ISO start time", () => {
  const start = "2026-10-15T00:00:00Z";
  const now = Date.parse(start) + 92_000;
  assert.equal(formatElapsed(start, now), "1m 32s");
  assert.equal(formatElapsed(start, Date.parse(start) + 4_000), "4s");
  assert.equal(formatElapsed(null, now), "");
});

test("formatDate is a stable, locale-independent label", () => {
  assert.equal(formatDate("2026-10-14T22:10:00Z"), "14 Oct 2026, 22:10");
  assert.equal(formatDate(null), "—");
});

test("formatBytes uses binary-ish units with one decimal", () => {
  assert.equal(formatBytes(0), "0 B");
  assert.equal(formatBytes(999), "999 B");
  assert.equal(formatBytes(4_404_019), "4.2 MB");
});

test("statusKind folds the server vocabulary into the UI's status set", () => {
  assert.equal(statusKind({ status: "ready" }), "ready");
  assert.equal(statusKind({ status: "processing" }), "processing");
  assert.equal(statusKind({ status: "failed" }), "failed");
  assert.equal(statusKind({ status: "uploaded", latest_job_state: "running" }), "processing");
  assert.equal(statusKind({ status: "uploaded", latest_job_state: "queued" }), "queued");
  assert.equal(statusKind({ status: "uploaded", latest_job_state: "failed" }), "failed");
  assert.equal(statusKind({ status: "uploaded" }), "unprocessed");
  assert.equal(statusKind({ status: "deleted" }), "deleted");
});

test("statusLabel gives the human wording", () => {
  assert.equal(statusLabel("ready"), "Ready");
  assert.equal(statusLabel("processing"), "Processing");
  assert.equal(statusLabel("queued"), "Queued");
  assert.equal(statusLabel("failed"), "Failed");
  assert.equal(statusLabel("unprocessed"), "Not processed");
});

test("isProcessing is true only for queued/running jobs", () => {
  assert.equal(isProcessing("queued"), true);
  assert.equal(isProcessing("running"), true);
  assert.equal(isProcessing("succeeded"), false);
  assert.equal(isProcessing(null), false);
});

test("describeJobState humanises a pipeline step state map", () => {
  assert.equal(describeJobState({ steps: { transcribe: "succeeded", diarize: "running" } }), "diarize running");
  assert.equal(describeJobState({ steps: { analyze: "queued" } }), "analyze queued");
  assert.equal(describeJobState({ steps: {} }), "");
});

test("friendlyError maps the error envelope codes", () => {
  assert.equal(friendlyError({ code: "not_found" }), "That meeting is not on the server.");
  assert.equal(friendlyError({ code: "session_expired" }), "Your session expired. Sign in again.");
  assert.equal(friendlyError({ code: "unauthorized" }), "Sign in to use the cloud.");
  assert.equal(friendlyError({ code: "no_analysis" }), "There is no current analysis to export yet.");
  assert.equal(friendlyError({ code: "network_error" }), "The service is unreachable.");
  assert.equal(friendlyError({ code: "who_knows" }), "who_knows");
});

test("pagingRange describes the page window", () => {
  assert.deepEqual(pagingRange({ total: 40, limit: 20, offset: 0 }), {
    from: 1,
    to: 20,
    total: 40,
    hasPrev: false,
    hasNext: true,
  });
  assert.deepEqual(pagingRange({ total: 40, limit: 20, offset: 20 }), {
    from: 21,
    to: 40,
    total: 40,
    hasPrev: true,
    hasNext: false,
  });
  assert.deepEqual(pagingRange({ total: 0, limit: 20, offset: 0 }), {
    from: 0,
    to: 0,
    total: 0,
    hasPrev: false,
    hasNext: false,
  });
});
