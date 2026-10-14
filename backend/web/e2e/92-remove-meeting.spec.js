import { test, expect, tokenOf } from "./fixtures.js";

// Creates its own throwaway meeting so the suite is repeatable without re-seeding.
test("removing a meeting from the cloud deletes it on the server", async ({ signedIn, consoleErrors }) => {
  const token = await tokenOf(signedIn);
  const auth = { Authorization: `Bearer ${token}` };
  const title = `Throwaway review ${Date.now()}`;
  const created = await signedIn.request.post("/v1/meetings", {
    headers: auth,
    data: { title, external_id: `e2e-${Date.now()}`, source: "meetvault" },
  });
  expect(created.status()).toBe(201);
  const meetingId = (await created.json()).id;

  await signedIn.goto("/#/meetings");
  const row = signedIn.locator(".row", { hasText: title });
  await expect(row).toBeVisible();

  await row.getByRole("button", { name: "Remove from cloud" }).click();
  const dialog = signedIn.getByTestId("remove-dialog");
  await expect(dialog).toContainText(title);
  await dialog.getByRole("button", { name: "Remove" }).click();

  await expect(signedIn.getByRole("status").filter({ hasText: "Removed from cloud." })).toBeVisible();
  await expect(row).toHaveCount(0);
  const after = await signedIn.request.get(`/v1/meetings/${meetingId}`, { headers: auth });
  expect(after.status()).toBe(404);
  expect(consoleErrors).toEqual([]);
});
