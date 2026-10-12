"use strict";

// Pure review planning: turn the model's JSON into the review event, the
// inline comments, and the review body. Kept out of the workflow so it can be
// exercised without GitHub.

const SEVERITIES = new Set(["critical", "important", "minor"]);
const BLOCKING = new Set(["critical", "important"]);

// Appended to a change request by the renderer, not produced by the model: the
// instructions an agent should follow when it picks the review up. Distilled
// from the repository's receiving-code-review guidance.
const TRIAGE_GUIDE = [
  "<details>",
  "<summary>Guide for agents triaging this review (click to expand)</summary>",
  "",
  "This review is one reviewer's output, not a source of authority. Treat every finding as a claim",
  "to check against the code, and change nothing until you have.",
  "",
  "1. Read the whole review before editing anything.",
  "2. Re-verify each finding in this repository. Reproduce it, or trace the exact path it names. A",
  "   finding that does not hold here is not a defect.",
  "3. If a finding is wrong or does not apply, say so with technical reasoning in that finding's",
  "   thread. Do not implement it to look responsive. Reply in the review thread, not as a new",
  "   top-level comment.",
  "4. If a finding is unclear, stop and ask before implementing. Items are often related, and a",
  "   partial understanding produces the wrong fix.",
  "5. Order the work: blocking issues (correctness, security, data) first, then simple fixes, then",
  "   structural ones. Change one item at a time and test each.",
  "6. Do not agree performatively. State the requirement, or fix it and show the code.",
  "7. A passing suite is not proof. Verify the behavior the finding describes.",
  "",
  "The reviewer can be wrong. Challenge it with evidence.",
  "</details>",
].join("\n");

/**
 * The new-file line numbers a review comment may anchor to, per changed file.
 * GitHub rejects the whole review when any comment names a line outside the
 * diff, so the caller filters against this first. A hunk header carries the
 * new-side start and count; every line it spans is anchorable.
 */
function anchorableLines(files) {
  const map = new Map();
  for (const file of files ?? []) {
    const lines = new Set();
    if (typeof file?.patch === "string") {
      const hunk = /^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@/gm;
      let match;
      while ((match = hunk.exec(file.patch)) !== null) {
        const start = Number(match[1]);
        const count = match[2] === undefined ? 1 : Number(match[2]);
        for (let offset = 0; offset < count; offset += 1) lines.add(start + offset);
      }
    }
    if (typeof file?.filename === "string") map.set(file.filename, lines);
  }
  return map;
}

/** Pull the JSON object out of the model's final message, tolerating prose. */
function extractPlan(text) {
  const raw = String(text ?? "");
  const start = raw.indexOf("{");
  const end = raw.lastIndexOf("}");
  if (start === -1 || end <= start) return null;
  try {
    return JSON.parse(raw.slice(start, end + 1));
  } catch {
    return null;
  }
}

/** Drop unusable findings and clamp severity to the schema's three values. */
function normalizeFindings(plan) {
  const findings = Array.isArray(plan?.findings) ? plan.findings : [];
  const normalized = [];
  for (const finding of findings) {
    if (!finding || typeof finding.path !== "string" || typeof finding.body !== "string") continue;
    const line = Number(finding.line);
    if (!Number.isInteger(line) || line <= 0) continue;
    normalized.push({
      path: finding.path,
      line,
      severity: SEVERITIES.has(finding.severity) ? finding.severity : "minor",
      body: finding.body,
    });
  }
  return normalized;
}

/** Decide the review event, the inline comments, and the verdict label. */
function planReview(modelText, files) {
  const plan = extractPlan(modelText);
  if (!plan) {
    return {
      parsed: false,
      event: "COMMENT",
      verdict: "Review could not be read",
      summary: String(modelText ?? "").trim(),
      comments: [],
      unanchored: [],
    };
  }
  const anchorable = anchorableLines(files);
  const findings = normalizeFindings(plan);
  const comments = [];
  const unanchored = [];
  for (const finding of findings) {
    const valid = anchorable.get(finding.path);
    if (valid && valid.has(finding.line)) {
      comments.push({
        path: finding.path,
        line: finding.line,
        side: "RIGHT",
        body: "**[" + finding.severity + "]** " + finding.body,
      });
    } else {
      unanchored.push(finding);
    }
  }
  const blocking = findings.some((finding) => BLOCKING.has(finding.severity));
  return {
    parsed: true,
    event: blocking ? "REQUEST_CHANGES" : "APPROVE",
    verdict: blocking ? "Change Needed" : "Accepted",
    summary: typeof plan.summary === "string" ? plan.summary : "",
    comments,
    unanchored,
  };
}

/**
 * The review body: the verdict headline, the summary, and any finding GitHub
 * could not anchor to a diff line. Capped so a huge review still posts.
 */
function reviewBody(plan, marker) {
  const lines = [marker, "### " + plan.verdict, ""];
  if (plan.summary) lines.push(plan.summary);
  if (plan.unanchored.length > 0) {
    lines.push("");
    lines.push("**Findings outside the diff, listed here because GitHub cannot anchor them:**");
    for (const finding of plan.unanchored) {
      lines.push("- \`[" + finding.severity + "] " + finding.path + ":" + finding.line + "\` " + finding.body);
    }
  }
  // Only a change request is handed to an agent for triage; an approval is
  // finished work.
  if (plan.event === "REQUEST_CHANGES") {
    // Cap the review, not the guide: the guide is appended after the cap so a
    // very long review cannot truncate the instructions that follow it.
    return lines.join("\n").slice(0, 65000) + "\n\n" + TRIAGE_GUIDE;
  }
  return lines.join("\n").slice(0, 65000);
}

module.exports = {
  anchorableLines,
  extractPlan,
  normalizeFindings,
  planReview,
  reviewBody,
  TRIAGE_GUIDE,
};
