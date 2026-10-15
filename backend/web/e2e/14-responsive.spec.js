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

test("the mobile header never breaks a word or a control", async ({ signedIn }) => {
  await expect(signedIn.locator(".row").first()).toBeVisible();
  const m = await signedIn.evaluate(() => {
    const mail = document.querySelector(".account .mail");
    const out = [...document.querySelectorAll("button")].find((b) => /sign out/i.test(b.textContent));
    const rail = document.querySelector(".rail");
    return {
      mailText: mail.textContent,
      mailLineBoxes: mail.getClientRects().length, // one rect per rendered line box
      mailClipped: mail.scrollWidth > mail.clientWidth + 1,
      signoutHeight: Math.round(out.getBoundingClientRect().height),
      signoutOneLine: Math.round(out.getBoundingClientRect().height) <= 40,
      railHeight: Math.round(rail.getBoundingClientRect().height),
      railScroll: rail.scrollWidth,
      railClient: rail.clientWidth,
    };
  });
  // A crushed flex child used to break the address mid-word ("demo@m / eetvault.lo / cal") and
  // split the Sign out control across three lines.
  expect(m.mailText).toBe("demo@meetvault.local");
  expect(m.mailLineBoxes).toBe(1);
  expect(m.mailClipped).toBe(false);
  expect(m.signoutOneLine, `sign out height ${m.signoutHeight}px`).toBe(true);
  expect(m.railScroll).toBeLessThanOrEqual(m.railClient + 1); // wraps, never scrolls sideways
  expect(m.railHeight).toBeLessThan(140); // and stays a header, not half the screen
});

test("the sign-in form fits a phone-width viewport", async ({ page }) => {
  await page.goto("/#/login");
  await expect(page.getByRole("button", { name: "Sign in" })).toBeVisible();
  const box = await page.getByRole("button", { name: "Sign in" }).boundingBox();
  expect(box.width).toBeLessThanOrEqual(420);
  expect(await overflowOf(page)).toBeLessThanOrEqual(1);
});
