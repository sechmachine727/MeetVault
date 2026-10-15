import { test, expect } from "./fixtures.js";

test("a processing row's elapsed time really counts up (auto-refresh)", async ({ signedIn, consoleErrors }) => {
  const row = signedIn.locator('.row[data-kind="processing"]');
  await expect(row).toHaveCount(1);
  const status = row.getByTestId("status");
  const first = await status.textContent();
  expect(first).toContain("Processing");
  expect(first).toMatch(/\d+m \d+s|\d+s/); // the elapsed timer is rendered
  await expect
    .poll(async () => status.textContent(), { timeout: 12_000, intervals: [500] })
    .not.toBe(first);
  expect(consoleErrors).toEqual([]);
});

test("an idle console does not poll per-meeting endpoints", async ({ signedIn }) => {
  // Only the finished meeting: nothing is moving, so the poll gate must stay closed.
  await signedIn.goto("/#/meetings?status=ready");
  await expect(signedIn.getByRole("heading", { name: "Meetings in the cloud" })).toBeVisible();
  await expect(signedIn.locator(".row")).toHaveCount(1);

  const perMeeting = [];
  signedIn.on("request", (request) => {
    if (request.url().includes("/v1/meetings/")) perMeeting.push(request.url());
  });
  await signedIn.waitForTimeout(6000);
  expect(perMeeting).toEqual([]);
});

test("while something is processing the console does poll /status", async ({ signedIn }) => {
  const perMeeting = [];
  signedIn.on("request", (request) => {
    if (request.url().includes("/v1/meetings/") && request.url().endsWith("/status")) {
      perMeeting.push(request.url());
    }
  });
  await signedIn.waitForTimeout(6000);
  expect(perMeeting.length).toBeGreaterThan(0);
});
