import test from "node:test";
import assert from "node:assert/strict";

import { parseHash, toHash } from "../lib/router.js";

test("an empty or root hash is the meetings list", () => {
  assert.deepEqual(parseHash(""), { name: "meetings", id: null, params: {} });
  assert.deepEqual(parseHash("#/"), { name: "meetings", id: null, params: {} });
  assert.deepEqual(parseHash("#/meetings"), { name: "meetings", id: null, params: {} });
});

test("a meeting route carries the id and the tab", () => {
  assert.deepEqual(parseHash("#/meetings/abc-123"), {
    name: "meeting",
    id: "abc-123",
    params: {},
  });
  assert.deepEqual(parseHash("#/meetings/abc-123?tab=transcript"), {
    name: "meeting",
    id: "abc-123",
    params: { tab: "transcript" },
  });
});

test("login and system are recognised", () => {
  assert.deepEqual(parseHash("#/login"), { name: "login", id: null, params: {} });
  assert.deepEqual(parseHash("#/system"), { name: "system", id: null, params: {} });
});

test("an unknown route falls back to the meetings list", () => {
  assert.deepEqual(parseHash("#/nope/deep"), { name: "meetings", id: null, params: {} });
});

test("toHash builds the canonical form", () => {
  assert.equal(toHash("meetings"), "#/meetings");
  assert.equal(toHash("meeting", { id: "m1" }), "#/meetings/m1");
  assert.equal(toHash("meeting", { id: "m1", params: { tab: "analysis" } }), "#/meetings/m1?tab=analysis");
});
