import { test, expect, openMeeting } from "./fixtures.js";

// Destructive: runs after the screenshots (see the file-name numbering) — re-seed before a re-run.
test("a server recording can be deleted, and the UI says so", async ({ signedIn, consoleErrors }) => {
  await openMeeting(signedIn, "Storage migration standup");
  const media = signedIn.getByTestId("media-list");
  await expect(media.locator(".row")).toHaveCount(1);

  await media.getByTestId("delete-media").click();
  await expect(signedIn.getByRole("status").filter({ hasText: "Recording deleted from the server." })).toBeVisible();
  await expect(media.locator(".row")).toHaveCount(0);
  await expect(media).toContainText("No recordings on the server");
  expect(consoleErrors).toEqual([]);
});
