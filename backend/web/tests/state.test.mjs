import test from "node:test";
import assert from "node:assert/strict";

import { createSessionStore, shouldPoll, POLL_MS } from "../lib/state.js";

function fakeStorage() {
  const map = new Map();
  return {
    getItem: (k) => (map.has(k) ? map.get(k) : null),
    setItem: (k, v) => map.set(k, String(v)),
    removeItem: (k) => map.delete(k),
  };
}

test("the session store round-trips the token and the user", () => {
  const store = createSessionStore(fakeStorage());
  assert.equal(store.getToken(), null);
  store.save("tok-1", { email: "a@b.c", display_name: "A" });
  assert.equal(store.getToken(), "tok-1");
  assert.equal(store.getUser().display_name, "A");
  store.clear();
  assert.equal(store.getToken(), null);
  assert.equal(store.getUser(), null);
});

test("a corrupt user blob does not throw", () => {
  const storage = fakeStorage();
  storage.setItem("mvweb.user", "{not json");
  const store = createSessionStore(storage);
  assert.equal(store.getUser(), null);
});

test("shouldPoll is true while any row has a queued or running job", () => {
  assert.equal(shouldPoll([]), false);
  assert.equal(shouldPoll([{ latest_job_state: "succeeded" }]), false);
  assert.equal(shouldPoll([{ latest_job_state: "succeeded" }, { latest_job_state: "running" }]), true);
  assert.equal(shouldPoll([{ latest_job_state: "queued" }]), true);
});

test("the poll interval is the contract's 2 s", () => {
  assert.equal(POLL_MS, 2000);
});
