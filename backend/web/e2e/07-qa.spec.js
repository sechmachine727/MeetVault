import { test, expect, openMeeting } from "./fixtures.js";

const READY = "Cloud console review";

test("the Q&A tab lists the stored answer with citations", async ({ signedIn, consoleErrors }) => {
  await openMeeting(signedIn, READY);
  await signedIn.getByRole("tab", { name: "Q&A" }).click();
  const history = signedIn.getByTestId("qa-history");
  await expect(history.locator(".row").first()).toBeVisible();
  await expect(history).toContainText("What did we decide about the release date?");
  await expect(history.getByTestId("qa-answer").first()).toContainText("[mock] answer");
  expect(await history.getByTestId("qa-citation").count()).toBeGreaterThan(0);
  expect(consoleErrors).toEqual([]);
});

test("a citation seeks the recording it came from", async ({ signedIn }) => {
  await openMeeting(signedIn, READY);
  await signedIn.getByRole("tab", { name: "Q&A" }).click();
  const player = signedIn.locator('[data-testid="player"]');
  await signedIn.getByTestId("qa-citation").first().click();
  await expect.poll(async () => player.evaluate((element) => element.currentTime), { timeout: 5000 }).toBeGreaterThan(0);
});

test("asking a new question queues a job and the worker answers it", async ({ signedIn }) => {
  await openMeeting(signedIn, READY);
  await signedIn.getByRole("tab", { name: "Q&A" }).click();
  const before = await signedIn.getByTestId("qa-history").locator(".row").count();

  await signedIn.getByLabel("Ask about this meeting").fill("What is the rollout plan?");
  const [request] = await Promise.all([
    signedIn.waitForRequest((r) => r.url().includes("/qa") && r.method() === "POST"),
    signedIn.getByTestId("ask").click(),
  ]);
  const payload = JSON.parse(request.postData() || "{}");
  expect(payload.question).toBe("What is the rollout plan?");
  expect(payload.top_k).toBe(8);
  await expect(signedIn.getByRole("status").filter({ hasText: "Question queued" })).toBeVisible();

  // The demo stack runs the real worker, so the answer lands (the tab re-renders on demand).
  await expect
    .poll(
      async () => {
        await signedIn.reload();
        await signedIn.getByRole("tab", { name: "Q&A" }).click();
        return signedIn.getByTestId("qa-history").locator(".row").count();
      },
      { timeout: 30_000, intervals: [2000] },
    )
    .toBeGreaterThan(before);
});
