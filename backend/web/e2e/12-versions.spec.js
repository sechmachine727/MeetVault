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
  const activeRow = signedIn.locator('[data-testid="versions"] .row', { has: signedIn.getByTestId("active-version") });
  await expect(activeRow).toContainText("#2 · diarized");

  await signedIn.getByTestId("activate-version").click();
  await expect(signedIn.getByRole("status").filter({ hasText: "Transcript activated." })).toBeVisible();
  // Exactly one version is active, so exactly one Activate button remains — on the other row.
  await expect(signedIn.getByTestId("activate-version")).toHaveCount(1);
  await expect(activeRow).toContainText("#1 · uploaded");

  // The imported (non-mock) transcript is now active: its text has no [mock: prefix at all.
  await signedIn.getByRole("tab", { name: "Transcript" }).click();
  const transcript = signedIn.getByTestId("transcript");
  await expect(transcript).toContainText("Welcome everyone");
  await expect(transcript).not.toContainText("[mock:");

  // REPORTED FINDING, pinned as observed rather than asserted as correct: activating a different
  // version does NOT mark the analysis stale. The server keeps fresh_kinds and every document's
  // stale flag exactly as they were (probe evidence in docs/web-report/REPORT.md), so the console
  // still presents an analysis derived from a transcript that is no longer active, with no
  // staleness signal. If the backend starts invalidating on activation, this assertion flips and
  // the banner appears — which is the intended fix.
  await expect(signedIn.getByTestId("stale-banner")).toHaveCount(0);
});

test("deleting the active version is refused with the server's own explanation", async ({ signedIn }) => {
  await openMeeting(signedIn, READY);
  await signedIn.getByRole("tab", { name: "Versions" }).click();
  const activeRow = signedIn.locator('[data-testid="versions"] .row', { has: signedIn.getByTestId("active-version") });
  await activeRow.getByTestId("delete-version").click();
  const dialog = signedIn.getByTestId("delete-version-dialog");
  await expect(dialog).toContainText("Delete transcript version");
  await dialog.getByRole("button", { name: "Delete version" }).click();
  await expect(
    signedIn.getByRole("status").filter({ hasText: "Cannot delete the active transcript" }),
  ).toBeVisible();
  await expect(signedIn.getByTestId("versions").locator(".row")).toHaveCount(2);
});

test("a non-active version can be deleted, after the cascade is disclosed", async ({ signedIn }) => {
  await openMeeting(signedIn, READY);
  await signedIn.getByRole("tab", { name: "Versions" }).click();
  const inactiveRow = signedIn
    .locator('[data-testid="versions"] .row')
    .filter({ hasNot: signedIn.getByTestId("active-version") })
    .first();
  await inactiveRow.getByTestId("delete-version").click();
  const dialog = signedIn.getByTestId("delete-version-dialog");
  // The dialog must name the consequence: the server cascades the analysis derived from it.
  await expect(dialog).toContainText("every analysis derived from it");
  await dialog.getByRole("button", { name: "Delete version" }).click();
  await expect(signedIn.getByRole("status").filter({ hasText: "Version deleted." })).toBeVisible();
  await expect(signedIn.getByTestId("versions").locator(".row")).toHaveCount(1);
});
