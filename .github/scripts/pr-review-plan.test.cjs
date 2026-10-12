"use strict";

// Runnable check: node --test .github/scripts/pr-review-plan.test.cjs

const test = require("node:test");
const assert = require("node:assert/strict");
const { anchorableLines, extractPlan, planReview, reviewBody } = require("./pr-review-plan.cjs");

const FILES = [
  {
    filename: "src/MeetVault/Views/DashboardPage.xaml.cs",
    patch:
      "@@ -70,3 +70,5 @@ namespace MeetVault.Views\n context A\n+added one\n+added two\n context B\n context C\n" +
      "@@ -120,2 +122,4 @@\n context D\n+later\n+later too\n context E\n",
  },
  { filename: "docs/COVERAGE.md", patch: null },
];

test("anchorableLines spans each hunk's new-side lines and skips patchless files", () => {
  const map = anchorableLines(FILES);
  assert.deepEqual(
    [...map.get("src/MeetVault/Views/DashboardPage.xaml.cs")].sort((a, b) => a - b),
    [70, 71, 72, 73, 74, 122, 123, 124, 125],
  );
  assert.equal(map.get("docs/COVERAGE.md").size, 0);
});

test("extractPlan reads the object out of surrounding prose", () => {
  const parsed = extractPlan('Review complete.\n{"summary":"fine","findings":[]}\nThanks.');
  assert.equal(parsed.summary, "fine");
  assert.equal(extractPlan("no object here"), null);
});

test("a critical or important finding produces a change request", () => {
  const model = JSON.stringify({
    summary: "Two problems.",
    findings: [{ path: "src/MeetVault/Views/DashboardPage.xaml.cs", line: 71, severity: "critical", body: "boom" }],
  });
  const plan = planReview(model, FILES);
  assert.equal(plan.event, "REQUEST_CHANGES");
  assert.equal(plan.verdict, "Change Needed");
  assert.equal(plan.comments.length, 1);
  assert.equal(plan.comments[0].line, 71);
  assert.equal(plan.comments[0].side, "RIGHT");
  assert.match(plan.comments[0].body, /\[critical\]/);
});

test("minor findings approve and still post inline", () => {
  const model = JSON.stringify({
    summary: "Small thing.",
    findings: [{ path: "src/MeetVault/Views/DashboardPage.xaml.cs", line: 122, severity: "minor", body: "nit" }],
  });
  const plan = planReview(model, FILES);
  assert.equal(plan.event, "APPROVE");
  assert.equal(plan.comments.length, 1);
  assert.equal(plan.unanchored.length, 0);
});

test("a finding outside the diff is unanchored instead of dropping the review", () => {
  const model = JSON.stringify({
    summary: "One anchored, one not.",
    findings: [
      { path: "src/MeetVault/Views/DashboardPage.xaml.cs", line: 9999, severity: "important", body: "off-diff" },
      { path: "docs/COVERAGE.md", line: 3, severity: "minor", body: "patchless file" },
    ],
  });
  const plan = planReview(model, FILES);
  assert.equal(plan.comments.length, 0);
  assert.equal(plan.unanchored.length, 2);
  assert.equal(plan.event, "REQUEST_CHANGES");
});

test("an empty finding list is an acceptance with no comments", () => {
  const plan = planReview('{"summary":"Nothing material.","findings":[]}', FILES);
  assert.equal(plan.event, "APPROVE");
  assert.equal(plan.verdict, "Accepted");
  assert.equal(plan.comments.length, 0);
});

test("unreadable output falls back to a comment that keeps the raw text", () => {
  const plan = planReview("the model refused", FILES);
  assert.equal(plan.parsed, false);
  assert.equal(plan.event, "COMMENT");
  assert.match(plan.summary, /refused/);
});

test("a missing severity is treated as minor rather than blocking", () => {
  const model = JSON.stringify({ summary: "s", findings: [{ path: "src/MeetVault/Views/DashboardPage.xaml.cs", line: 70, body: "no severity" }] });
  const plan = planReview(model, FILES);
  assert.equal(plan.comments.length, 1);
  assert.equal(plan.event, "APPROVE");
});

test("reviewBody leads with the verdict, keeps the marker, and lists unanchored findings", () => {
  const model = JSON.stringify({
    summary: "Mixed.",
    findings: [{ path: "docs/COVERAGE.md", line: 3, severity: "minor", body: "cannot anchor" }],
  });
  const body = reviewBody(planReview(model, FILES), "<!-- codex-pr-review -->");
  assert.match(body, /<!-- codex-pr-review -->/);
  assert.match(body, /### Accepted/);
  assert.match(body, /Mixed\./);
  assert.match(body, /docs\/COVERAGE\.md:3/);
});

test("a change request carries the agent triage guide, an approval does not", () => {
  const blocking = JSON.stringify({
    summary: "Needs work.",
    findings: [{ path: "src/MeetVault/Views/DashboardPage.xaml.cs", line: 71, severity: "important", body: "issue" }],
  });
  const changeBody = reviewBody(planReview(blocking, FILES), "<!-- codex-pr-review -->");
  assert.match(changeBody, /### Change Needed/);
  assert.match(changeBody, /<details>/);
  assert.match(changeBody, /not a source of authority/i);
  assert.match(changeBody, /Challenges|Challenge it/i);

  const ok = reviewBody(planReview('{"summary":"fine","findings":[]}', FILES), "<!-- codex-pr-review -->");
  assert.equal(ok.includes("<details>"), false);
});

test("unreadable output is not treated as a change request", () => {
  const plan = planReview("the model refused", FILES);
  assert.notEqual(plan.event, "REQUEST_CHANGES");
  assert.equal(reviewBody(plan, "m").includes("<details>"), false);
});
