import { test, expect, EMAIL, PASSWORD } from "./fixtures.js";

// Sign-up is additive: every test here creates its OWN account, so it cannot disturb the seeded
// demo dataset the numbered specs mutate. Runs right after 01-auth (alphabetical order).

const newEmail = () => `signup-${Date.now()}-${Math.floor(Math.random() * 1e6)}@example.com`;

test("the sign-in screen links to sign-up and back", async ({ page }) => {
  await page.goto("/#/login");
  await page.getByRole("link", { name: "Create an account" }).click();
  await expect(page.getByRole("heading", { name: "Create your account" })).toBeVisible();
  await page.getByRole("link", { name: "Already have an account? Sign in" }).click();
  await expect(page.getByRole("heading", { name: "Sign in to the cloud" })).toBeVisible();
});

test("a short or mismatched password is refused before it reaches the server", async ({ page }) => {
  await page.goto("/#/signup");
  await page.getByTestId("signup-email").fill(newEmail());
  await page.getByTestId("signup-password").fill("short");
  await page.getByTestId("signup-confirm").fill("short");
  await page.getByTestId("signup-submit").click();
  await expect(page.getByTestId("signup-error")).toHaveText("Password must be at least 8 characters.");

  await page.getByTestId("signup-password").fill("long-enough-1");
  await page.getByTestId("signup-confirm").fill("long-enough-2");
  await page.getByTestId("signup-submit").click();
  await expect(page.getByTestId("signup-error")).toHaveText("Those passwords do not match.");
  // still on the form: nothing was created
  await expect(page.getByRole("heading", { name: "Create your account" })).toBeVisible();
});

test("signing up with an email creates the account and lands on an empty cloud", async ({
  page,
  consoleErrors,
}) => {
  const email = newEmail();
  await page.goto("/#/signup");
  await page.getByTestId("signup-name").fill("Signup Tester");
  await page.getByTestId("signup-email").fill(email);
  await page.getByTestId("signup-password").fill("signup-password-1");
  await page.getByTestId("signup-confirm").fill("signup-password-1");
  await page.getByTestId("signup-submit").click();

  await expect(page.getByRole("heading", { name: "Meetings in the cloud" })).toBeVisible();
  await expect(page.locator(".account .mail")).toHaveText(email);
  await expect(page.locator(".account .who")).toHaveText("Signup Tester");
  // The new account must NOT see the demo account's three seeded meetings.
  await expect(page.getByTestId("empty")).toContainText("No meetings on the server yet.");
  await expect(page.locator(".row")).toHaveCount(0);
  expect(consoleErrors).toEqual([]);
});

test("the new credentials work after signing out", async ({ page }) => {
  const email = newEmail();
  await page.goto("/#/signup");
  await page.getByTestId("signup-email").fill(email);
  await page.getByTestId("signup-password").fill("signup-password-1");
  await page.getByTestId("signup-confirm").fill("signup-password-1");
  await page.getByTestId("signup-submit").click();
  await expect(page.getByRole("heading", { name: "Meetings in the cloud" })).toBeVisible();

  await page.getByRole("button", { name: "Sign out" }).click();
  await expect(page.getByRole("heading", { name: "Sign in to the cloud" })).toBeVisible();
  await page.getByLabel("Email").fill(email);
  await page.getByLabel("Password").fill("signup-password-1");
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page.getByRole("heading", { name: "Meetings in the cloud" })).toBeVisible();
  await expect(page.locator(".account .mail")).toHaveText(email);
});

test("an email that already has an account is refused with a friendly message", async ({ page }) => {
  await page.goto("/#/signup");
  await page.getByTestId("signup-email").fill(EMAIL); // the seeded demo account
  await page.getByTestId("signup-password").fill(PASSWORD);
  await page.getByTestId("signup-confirm").fill(PASSWORD);
  await page.getByTestId("signup-submit").click();
  await expect(page.getByTestId("signup-error")).toHaveText("That email already has an account. Sign in instead.");
  await expect(page.getByRole("heading", { name: "Create your account" })).toBeVisible();
});
