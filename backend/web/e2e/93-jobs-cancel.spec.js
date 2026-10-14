import { test, expect, openMeeting, tokenOf } from "./fixtures.js";

const RUNNING = "Customer feedback sync";

// Last spec on purpose: it cancels the seeded `running` job, which the earlier specs (and the
// screenshots) rely on being mid-flight.
test("cancelling a running job is cooperative and it leaves the processing state", async ({
  signedIn,
  consoleErrors,
}) => {
  const token = await tokenOf(signedIn);
  const auth = { Authorization: `Bearer ${token}` };

  await openMeeting(signedIn, RUNNING);
  const meetingId = signedIn.url().split("/").pop().split("?")[0];

  await signedIn.getByRole("tab", { name: "Jobs" }).click();
  const [request] = await Promise.all([
    signedIn.waitForRequest((r) => r.url().includes("/cancel") && r.method() === "POST"),
    signedIn.getByTestId("cancel-job").click(),
  ]);
  expect(request.url()).toContain("/v1/jobs/");
  await expect(signedIn.getByRole("status").filter({ hasText: "Cancellation requested." })).toBeVisible();

  // The server really cancelled it (not just a UI toast).
  await expect
    .poll(
      async () => {
        const response = await signedIn.request.get(`/v1/meetings/${meetingId}/jobs`, { headers: auth });
        const page = await response.json();
        return page.items.map((job) => `${job.type}:${job.state}`).join(",");
      },
      { timeout: 15_000 },
    )
    .toContain("cancelled");

  // And the list stops showing it as processing.
  await signedIn.goto("/#/meetings");
  await expect(signedIn.locator('.row[data-kind="processing"]')).toHaveCount(0);
  expect(consoleErrors).toEqual([]);
});
