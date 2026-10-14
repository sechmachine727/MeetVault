import { test, expect, openMeeting } from "./fixtures.js";

const READY = "Cloud console review";

test("the versions tab lists every transcript version and marks the active one", async ({
  signedIn,
  consoleErrors,
}) => {
  await openMeeting(signedIn, READY);
  await signedIn.getByRole("tab", { name: "Versions" }).click();
  const rows = signedIn.getByTestId("versions").locator(".row");
  await expect(rows).toHaveCount(2); // the client's import + the pipeline's ASR version
  await expect(signedIn.getByTestId("active-version")).toHaveCount(1);
  await expect(signedIn.getByTestId("activate-version")).toHaveCount(1);
  await expect(signedIn.getByTestId("versions")).toContainText("#1 · uploaded");
  await expect(signedIn.getByTestId("versions")).toContainText("#2 · diarized");
  expect(consoleErrors).toEqual([]);
});

test("activating another version rolls the active transcript back", async ({ signedIn }) => {
  await openMeeting(signedIn, READY);
  await signedIn.getByRole("tab", { name: "Versions" }).click();
  await signedIn.getByTestId("activate-version").click();
  await expect(signedIn.getByRole("status").filter({ hasText: "Transcript activated." })).toBeVisible();
  await expect(signedIn.getByTestId("activate-version")).toHaveCount(0);

  // The imported (non-mock) transcript is now active: its text has no [mock: prefix at all.
  await signedIn.getByRole("tab", { name: "Transcript" }).click();
  const transcript = signedIn.getByTestId("transcript");
  await expect(transcript).toContainText("Welcome everyone");
  await expect(transcript).not.toContainText("[mock:");
});

test("deleting the active version is refused with the server's own explanation", async ({ signedIn }) => {
  await openMeeting(signedIn, READY);
  await signedIn.getByRole("tab", { name: "Versions" }).click();
  const activeRow = signedIn.locator('[data-testid="versions"] .row', { has: signedIn.getByTestId("active-version") });
  await activeRow.getByTestId("delete-version").click();
  await expect(
    signedIn.getByRole("status").filter({ hasText: "Cannot delete the active transcript" }),
  ).toBeVisible();
  await expect(signedIn.getByTestId("versions").locator(".row")).toHaveCount(2);
});

test("a non-active version can be deleted", async ({ signedIn }) => {
  await openMeeting(signedIn, READY);
  await signedIn.getByRole("tab", { name: "Versions" }).click();
  const inactiveRow = signedIn
    .locator('[data-testid="versions"] .row')
    .filter({ hasNot: signedIn.getByTestId("active-version") })
    .first();
  await inactiveRow.getByTestId("delete-version").click();
  await expect(signedIn.getByRole("status").filter({ hasText: "Version deleted." })).toBeVisible();
  await expect(signedIn.getByTestId("versions").locator(".row")).toHaveCount(1);
});
