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
  parseServerTime,
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

test("a naive server timestamp is read as UTC, never as local time", () => {
  // SQLite hands DateTime(timezone=True) back naive, so /status can emit no zone designator.
  // Date.parse would read it as local time (a 7-hour error in UTC+07:00).
  const naive = "2026-10-15T00:00:00";
  const now = Date.parse("2026-10-15T00:01:32Z");
  assert.equal(formatElapsed(naive, now), "1m 32s");
  assert.equal(parseServerTime(naive), "2026-10-15T00:00:00Z");
  assert.equal(parseServerTime("2026-10-15T00:00:00Z"), "2026-10-15T00:00:00Z");
  assert.equal(parseServerTime("2026-10-15T07:00:00+07:00"), "2026-10-15T07:00:00+07:00");
  assert.equal(formatDate("2026-10-15T00:31:00"), "15 Oct 2026, 00:31");
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

test("friendlyError falls back to the server's own message for codes it does not know", () => {
  // e.g. DELETE on the active transcript answers 409 with code "conflict" and a real sentence.
  assert.equal(
    friendlyError({ code: "conflict", message: "Cannot delete the active transcript; activate another version first" }),
    "Cannot delete the active transcript; activate another version first",
  );
  assert.equal(friendlyError({ code: "conflict" }), "conflict");
  assert.equal(friendlyError({ code: "not_found", message: "Meeting not found" }), "That meeting is not on the server.");
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
