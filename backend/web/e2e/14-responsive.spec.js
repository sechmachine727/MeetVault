import { test, expect, openMeeting } from "./fixtures.js";

// Runs in the `narrow` project (420x900) — see playwright.config.js.

const overflowOf = (page) =>
  page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);

test("the narrow layout does not overflow horizontally", async ({ signedIn, consoleErrors }) => {
  await expect(signedIn.getByRole("heading", { name: "Meetings in the cloud" })).toBeVisible();
  await expect(signedIn.locator(".row").first()).toBeVisible();
  expect(await overflowOf(signedIn)).toBeLessThanOrEqual(1);

  // The rail collapses to a top bar and stays reachable.
  await expect(signedIn.getByRole("link", { name: "Service" })).toBeVisible();
  expect(consoleErrors).toEqual([]);
});

test("a meeting detail stays usable and un-clipped at 420px", async ({ signedIn }) => {
  await openMeeting(signedIn, "Cloud console review");
  await signedIn.getByRole("tab", { name: "Transcript" }).click();
  await expect(signedIn.getByTestId("transcript").locator("table.segments tbody tr").first()).toBeVisible();
  expect(await overflowOf(signedIn)).toBeLessThanOrEqual(1);

  await signedIn.getByRole("tab", { name: "Analysis" }).click();
  await expect(signedIn.getByTestId("summary-bullets")).toBeVisible();
  expect(await overflowOf(signedIn)).toBeLessThanOrEqual(1);
});

test("the sign-in form fits a phone-width viewport", async ({ page }) => {
  await page.goto("/#/login");
  await expect(page.getByRole("button", { name: "Sign in" })).toBeVisible();
  const box = await page.getByRole("button", { name: "Sign in" }).boundingBox();
  expect(box.width).toBeLessThanOrEqual(420);
  expect(await overflowOf(page)).toBeLessThanOrEqual(1);
});
