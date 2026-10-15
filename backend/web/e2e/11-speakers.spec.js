import { test, expect, openMeeting } from "./fixtures.js";

const READY = "Cloud console review";

test("the speakers tab lists speakers with their segment counts", async ({ signedIn, consoleErrors }) => {
  await openMeeting(signedIn, READY);
  await signedIn.getByRole("tab", { name: "Speakers" }).click();
  const rows = signedIn.getByTestId("speakers").locator(".row");
  await expect(rows).toHaveCount(2);
  await expect(rows.first().locator(".meta")).toContainText("segment(s)");
  expect(consoleErrors).toEqual([]);
});

test("renaming a speaker relabels its segments in the transcript", async ({ signedIn }) => {
  await openMeeting(signedIn, READY);
  await signedIn.getByRole("tab", { name: "Speakers" }).click();
  const first = signedIn.getByTestId("speakers").locator(".row").first();
  await first.getByRole("textbox", { name: /Rename/ }).fill("Alice");
  await first.getByTestId("rename-speaker").click();
  await expect(signedIn.getByRole("status").filter({ hasText: "Speaker renamed." })).toBeVisible();
  await expect(signedIn.getByTestId("speakers").locator(".row").first().locator(".title")).toHaveText("Alice");

  await signedIn.getByRole("tab", { name: "Transcript" }).click();
  await expect(signedIn.getByTestId("transcript")).toContainText("Alice");
});

test("merging folds a duplicate speaker into another and the count drops", async ({ signedIn }) => {
  await openMeeting(signedIn, READY);
  await signedIn.getByRole("tab", { name: "Speakers" }).click();
  const rows = signedIn.getByTestId("speakers").locator(".row");
  await expect(rows).toHaveCount(2);
  const source = rows.first();
  const targetLabel = await rows.nth(1).locator(".title").textContent();
  await source.getByRole("combobox").selectOption({ index: 1 });
  await source.getByTestId("merge-speaker").click();
  await expect(signedIn.getByRole("status").filter({ hasText: "Speakers merged." })).toBeVisible();
  await expect(signedIn.getByTestId("speakers").locator(".row")).toHaveCount(1);
  await expect(signedIn.getByTestId("speakers").locator(".row").first().locator(".title")).toHaveText(targetLabel);
});

test("deleting the last speaker leaves the transcript text intact", async ({ signedIn }) => {
  await openMeeting(signedIn, READY);
  await signedIn.getByRole("tab", { name: "Speakers" }).click();
  await signedIn.getByTestId("speakers").locator(".row").first().getByTestId("delete-speaker").click();
  await expect(signedIn.getByRole("status").filter({ hasText: "Speaker removed." })).toBeVisible();
  await expect(signedIn.getByTestId("speakers").locator(".row")).toHaveCount(0);
  await expect(signedIn.getByTestId("speakers")).toContainText("No speakers on this transcript.");

  await signedIn.getByRole("tab", { name: "Transcript" }).click();
  await expect(signedIn.getByTestId("transcript").locator("table.segments tbody tr").first()).toBeVisible();
  await expect(signedIn.getByTestId("transcript")).toContainText("[mock:");
});
