"use strict";

// Single source of truth for the confirmation checklist that gates a pull
// request's return from draft. Both workflows that touch the block require this
// module, so the block text and the parsing rules cannot drift apart.

const START = "<!-- codex-ready-checklist:start -->";
const END = "<!-- codex-ready-checklist:end -->";
const BOX = /^\s*- \[[ xX]\] .+$/gm;
const TICKED = /^\s*- \[[xX]\] /;

const CHECKLIST_BLOCK = [
  START,
  "## Ready to review",
  "",
  "Tick every box once the findings are addressed. The pull request re-opens for",
  "review when the last box is ticked, and a new push clears them again.",
  "",
  "- [ ] No secrets, keys, or credentials in the diff",
  "- [ ] Security-relevant paths reviewed and hardened",
  "- [ ] Every change request addressed and every review thread resolved",
  "- [ ] Notes and docs updated for this change",
  "- [ ] Scope still matches the pull request description",
  "- [ ] The branch is current with main, not 5 or more commits behind",
  END,
].join("\n");

/** The marked region and its offsets, or null when the body carries no block. */
function regionOf(body) {
  const text = String(body ?? "");
  const start = text.indexOf(START);
  const end = text.indexOf(END);
  if (start === -1 || end === -1 || end < start) return null;
  return { text, start, end, region: text.slice(start + START.length, end) };
}

/** Count the boxes without assuming how many the block carries. */
function parseChecklist(body) {
  const found = regionOf(body);
  if (!found) return { present: false, total: 0, checked: 0, complete: false };
  const boxes = found.region.match(BOX) ?? [];
  const checked = boxes.filter((line) => TICKED.test(line)).length;
  return {
    present: true,
    total: boxes.length,
    checked,
    complete: boxes.length > 0 && checked === boxes.length,
  };
}

/** Append the block when the body does not carry one yet. */
function ensureChecklist(body) {
  const text = String(body ?? "");
  if (regionOf(text)) return text;
  const head = text.replace(/\s+$/, "");
  return `${head ? `${head}\n\n` : ""}${CHECKLIST_BLOCK}\n`;
}

/** Clear every tick so the next review starts from an unconfirmed state. */
function resetChecklist(body) {
  const original = String(body ?? "");
  const ensured = ensureChecklist(original);
  const found = regionOf(ensured);
  const reset = found.region.replace(/^(\s*- \[)[xX](\])/gm, "$1 $2");
  if (reset === found.region) return { body: ensured, changed: ensured !== original };
  return {
    body: ensured.slice(0, found.start + START.length) + reset + ensured.slice(found.end),
    changed: true,
  };
}

module.exports = {
  START,
  END,
  CHECKLIST_BLOCK,
  parseChecklist,
  ensureChecklist,
  resetChecklist,
};
