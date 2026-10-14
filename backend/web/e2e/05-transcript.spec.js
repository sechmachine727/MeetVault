import { test, expect, openMeeting } from "./fixtures.js";

const READY = "Cloud console review";

async function waitForMedia(page) {
  await page.locator('[data-testid="player"]').evaluate(
    (element) =>
      new Promise((resolve) => {
        if (element.readyState >= 1) {
          resolve(element.duration);
          return;
        }
        element.addEventListener("loadedmetadata", () => resolve(element.duration), { once: true });
        element.addEventListener("error", () => resolve(-1), { once: true });
        setTimeout(() => resolve(element.duration), 8000);
      }),
  );
}

test("the transcript tab renders the active version with speakers and times", async ({ signedIn, consoleErrors }) => {
  await openMeeting(signedIn, READY);
  await signedIn.getByRole("tab", { name: "Transcript" }).click();
  const table = signedIn.getByTestId("transcript").locator("table.segments");
  await expect(table).toBeVisible();
  const rows = table.locator("tbody tr");
  expect(await rows.count()).toBeGreaterThanOrEqual(2);
  await expect(signedIn.getByTestId("transcript")).toContainText("segment(s)");
  // the ASR transcript is mock-labelled server-side and must not be disguised
  await expect(signedIn.getByTestId("transcript")).toContainText("[mock:");
  // a speaker label per row
  const speaker = await rows.first().locator("td.speaker").textContent();
  expect(speaker).toMatch(/^Speaker \d+$/);
  expect(consoleErrors).toEqual([]);
});

test("clicking a timestamp seeks the recording", async ({ signedIn }) => {
  await openMeeting(signedIn, READY);
  await signedIn.getByRole("tab", { name: "Transcript" }).click();
  await waitForMedia(signedIn);
  const player = signedIn.locator('[data-testid="player"]');
  expect(await player.evaluate((element) => element.currentTime)).toBe(0);
  const lastRow = signedIn.getByTestId("transcript").locator("table.segments tbody tr").last();
  await lastRow.getByTestId("seek").click();
  await expect.poll(async () => player.evaluate((element) => element.currentTime), { timeout: 5000 }).toBeGreaterThan(0);
});

test("Copy transcript text puts the transcript on the clipboard", async ({ signedIn }) => {
  await signedIn.context().grantPermissions(["clipboard-read", "clipboard-write"]);
  await openMeeting(signedIn, READY);
  await signedIn.getByRole("tab", { name: "Transcript" }).click();
  await signedIn.getByTestId("copy-text").click();
  await expect(signedIn.getByRole("status").filter({ hasText: "Transcript copied." })).toBeVisible();
  const clipboard = await signedIn.evaluate(() => navigator.clipboard.readText());
  expect(clipboard.length).toBeGreaterThan(20);
  expect(clipboard).toContain("Speaker");
});
