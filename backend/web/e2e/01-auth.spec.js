import { test, expect, EMAIL, PASSWORD } from "./fixtures.js";

test("an unauthenticated visitor is asked to sign in", async ({ page, consoleErrors }) => {
  await page.goto("/#/meetings");
  await expect(page.getByRole("heading", { name: "Sign in to the cloud" })).toBeVisible();
  expect(consoleErrors).toEqual([]);
});

test("a wrong password shows the friendly message and stays put", async ({ page }) => {
  await page.goto("/#/login");
  await page.getByLabel("Email").fill(EMAIL);
  await page.getByLabel("Password").fill("definitely-the-wrong-password");
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page.getByText("That email and password do not match.")).toBeVisible();
  await expect(page.getByRole("heading", { name: "Sign in to the cloud" })).toBeVisible();
});

test("signing in shows the account and the list; signing out returns to sign-in", async ({
  signedIn,
  consoleErrors,
}) => {
  await expect(signedIn.locator(".account .mail")).toHaveText(EMAIL);
  await signedIn.getByRole("button", { name: "Sign out" }).click();
  await expect(signedIn.getByRole("heading", { name: "Sign in to the cloud" })).toBeVisible();
  const token = await signedIn.evaluate(() => window.localStorage.getItem("mvweb.token"));
  expect(token).toBeNull();
  expect(consoleErrors).toEqual([]);
});

test("the session survives a reload and a bad token is dropped", async ({ signedIn }) => {
  await signedIn.reload();
  await expect(signedIn.getByRole("heading", { name: "Meetings in the cloud" })).toBeVisible();
  await signedIn.evaluate(() => window.localStorage.setItem("mvweb.token", "not-a-real-token"));
  await signedIn.reload();
  await expect(signedIn.getByRole("heading", { name: "Sign in to the cloud" })).toBeVisible();
  expect(await signedIn.evaluate(() => window.localStorage.getItem("mvweb.token"))).toBeNull();
});

test("the demo account really is required (password unchanged from the seed)", async ({ page }) => {
  const response = await page.request.post("/v1/auth/login", {
    data: { email: EMAIL, password: PASSWORD },
  });
  expect(response.status()).toBe(200);
  const body = await response.json();
  expect(body.user.email).toBe(EMAIL);
  expect(body.token.length).toBeGreaterThan(20);
});
