import { test as base, expect } from "@playwright/test";

export const EMAIL = process.env.MVB_DEMO_EMAIL || "demo@meetvault.local";
export const PASSWORD = process.env.MVB_DEMO_PASSWORD || "demo-password-123";
// Written by seed_demo.py ("pinned_running"); the auto-refresh spec needs it.
export const PINNED_RUNNING_ID = process.env.PINNED_RUNNING_ID || "";

export const test = base.extend({
  // Any console error (including a CSP violation, which never surfaces as a failed request)
  // fails the test that produced it.
  consoleErrors: async ({ page }, use) => {
    const errors = [];
    page.on("console", (message) => {
      if (message.type() === "error") errors.push(message.text());
    });
    page.on("pageerror", (error) => errors.push(String(error)));
    await use(errors);
  },
  signedIn: async ({ page, consoleErrors }, use) => {
    void consoleErrors;
    await page.goto("/#/login");
    await page.getByLabel("Email").fill(EMAIL);
    await page.getByLabel("Password").fill(PASSWORD);
    await page.getByRole("button", { name: "Sign in" }).click();
    await expect(page.getByRole("heading", { name: "Meetings in the cloud" })).toBeVisible();
    await use(page);
  },
});

export async function openMeeting(page, title) {
  await page.locator(".row", { hasText: title }).getByRole("button", { name: "Open" }).click();
  await expect(page.getByRole("heading", { name: title, level: 1 })).toBeVisible();
}

export async function tokenOf(page) {
  return page.evaluate(() => window.localStorage.getItem("mvweb.token"));
}

export { expect };
