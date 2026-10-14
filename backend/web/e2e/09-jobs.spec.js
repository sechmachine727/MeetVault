import { test, expect, openMeeting } from "./fixtures.js";

const READY = "Cloud console review";
const RUNNING = "Customer feedback sync";

test("the jobs tab lists the pipeline history with state and mock marking", async ({ signedIn, consoleErrors }) => {
  await openMeeting(signedIn, READY);
  await signedIn.getByRole("tab", { name: "Jobs" }).click();
  const jobs = signedIn.getByTestId("jobs");
  await expect(jobs).toBeVisible();
  const rows = jobs.locator(".row");
  expect(await rows.count()).toBeGreaterThanOrEqual(4);
  await expect(jobs).toContainText("transcribe — succeeded");
  await expect(jobs).toContainText("analyze — succeeded");
  await expect(jobs).toContainText("(mock)");
  expect(consoleErrors).toEqual([]);
});

test("watching a finished job streams its stored events over SSE", async ({ signedIn }) => {
  await openMeeting(signedIn, READY);
  await signedIn.getByRole("tab", { name: "Jobs" }).click();
  // Target the transcribe job deterministically (the newest row is whichever step ran last).
  const row = signedIn.locator('[data-testid="jobs"] .row', { hasText: "transcribe — succeeded" });
  await expect(row).toHaveCount(1);
  await row.getByTestId("watch-job").click();
  const output = signedIn.getByTestId("job-events");
  await expect.poll(async () => (await output.textContent()).length, { timeout: 15_000 }).toBeGreaterThan(0);
  const text = await output.textContent();
  expect(text).toContain("data:");
  expect(text).toContain("queued transcribe");
  expect(text).toContain("started");
});

test("cancel is offered for a running job and withheld for finished ones", async ({ signedIn }) => {
  await openMeeting(signedIn, READY);
  await signedIn.getByRole("tab", { name: "Jobs" }).click();
  await expect(signedIn.getByTestId("cancel-job")).toHaveCount(0);

  await signedIn.goto("/#/meetings");
  await openMeeting(signedIn, RUNNING);
  await signedIn.getByRole("tab", { name: "Jobs" }).click();
  await expect(signedIn.getByTestId("cancel-job")).toHaveCount(1);
  await expect(signedIn.getByTestId("jobs").locator(".row").first()).toContainText("analyze — running");
});
