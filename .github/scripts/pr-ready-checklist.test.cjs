"use strict";

// Runnable check: node --test .github/scripts/pr-ready-checklist.test.cjs

const test = require("node:test");
const assert = require("node:assert/strict");
const {
  CHECKLIST_BLOCK,
  parseChecklist,
  ensureChecklist,
  resetChecklist,
} = require("./pr-ready-checklist.cjs");

const ITEMS = 5;

test("a body with no block reports the checklist as missing", () => {
  assert.deepEqual(parseChecklist("just a description"), {
    present: false,
    total: 0,
    checked: 0,
    complete: false,
  });
});

test("ensureChecklist appends the block once and is then a no-op", () => {
  const appended = ensureChecklist("description text");
  assert.equal(parseChecklist(appended).present, true);
  assert.match(appended, /description text/);
  assert.equal(ensureChecklist(appended), appended);
});

test("a partly ticked block is present but not complete", () => {
  const partly = CHECKLIST_BLOCK.replace("- [ ] No secrets", "- [x] No secrets");
  const read = parseChecklist(partly);
  assert.equal(read.present, true);
  assert.equal(read.total, ITEMS);
  assert.equal(read.checked, 1);
  assert.equal(read.complete, false);
});

test("a fully ticked block is complete", () => {
  const all = CHECKLIST_BLOCK.replace(/- \[ \]/g, "- [x]");
  const read = parseChecklist(all);
  assert.equal(read.checked, ITEMS);
  assert.equal(read.complete, true);
});

test("resetChecklist clears every tick and keeps the surrounding body", () => {
  const all = ensureChecklist("lead paragraph\n\n" + CHECKLIST_BLOCK.replace(/- \[ \]/g, "- [x]"));
  const { body, changed } = resetChecklist(all);
  assert.equal(changed, true);
  assert.match(body, /lead paragraph/);
  const read = parseChecklist(body);
  assert.equal(read.checked, 0);
  assert.equal(read.total, ITEMS);
  assert.equal(read.complete, false);
});

test("resetChecklist appends an unticked block to a body without one", () => {
  const { body, changed } = resetChecklist("only prose");
  assert.equal(changed, true);
  assert.match(body, /only prose/);
  const read = parseChecklist(body);
  assert.equal(read.present, true);
  assert.equal(read.checked, 0);
});

test("a body already clear of ticks is reported as unchanged", () => {
  const body = ensureChecklist(CHECKLIST_BLOCK);
  assert.equal(resetChecklist(body).changed, false);
});
