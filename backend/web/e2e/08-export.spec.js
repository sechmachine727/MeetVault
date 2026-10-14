import fs from "node:fs";
import path from "node:path";

import { test, expect, openMeeting } from "./fixtures.js";

const READY = "Cloud console review";
const ARTIFACTS = path.resolve(process.env.MVB_ARTIFACT_DIR || "../test-results/downloads");

test("Export to Word downloads a real .docx", async ({ signedIn, consoleErrors }) => {
  fs.mkdirSync(ARTIFACTS, { recursive: true });
  await openMeeting(signedIn, READY);
  const [download] = await Promise.all([
    signedIn.waitForEvent("download"),
    signedIn.getByRole("button", { name: "Export to Word" }).click(),
  ]);
  const suggested = download.suggestedFilename();
  expect(suggested).toMatch(/\.docx$/);
  expect(suggested).toContain("Cloud-console-review");
  const target = path.join(ARTIFACTS, "meeting-export.docx");
  await download.saveAs(target);
  const stat = fs.statSync(target);
  expect(stat.size).toBeGreaterThan(2000);
  expect(await signedIn.getByRole("status").filter({ hasText: "Word export downloaded." })).toBeVisible();
  expect(consoleErrors).toEqual([]);
});

test("exporting a meeting with no current analysis explains why instead of failing silently", async ({
  signedIn,
}) => {
  await openMeeting(signedIn, "Storage migration standup");
  await signedIn.getByRole("button", { name: "Export to Word" }).click();
  await expect(
    signedIn.getByRole("status").filter({ hasText: "There is no current analysis to export yet." }),
  ).toBeVisible();
});
