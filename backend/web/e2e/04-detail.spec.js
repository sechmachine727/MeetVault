import { test, expect, openMeeting } from "./fixtures.js";

const READY = "Cloud console review";

test("opening a meeting shows the header, the facts and the player", async ({ signedIn, consoleErrors }) => {
  await openMeeting(signedIn, READY);
  await expect(signedIn.getByTestId("status")).toContainText("Ready");
  await expect(signedIn.getByTestId("player-bar")).toContainText("Playing:");

  const subtitle = signedIn.locator("p.sub").first();
  await expect(subtitle).toContainText("2 recording(s)");
  await expect(subtitle).toContainText("transcript version(s)");

  const facts = signedIn.getByTestId("overview");
  await expect(facts).toContainText("ready"); // the raw server status
  await expect(facts).toContainText("0:30");
  await expect(facts).toContainText("current"); // analysis is current
  // The header badge names exactly what is mock: the ACTIVE TRANSCRIPT's provider.
  await expect(signedIn.getByTestId("mock-badge")).toHaveText("Mock transcript");
  expect(consoleErrors).toEqual([]);
});

test("the overview lists both server recordings — the audio and the screen track — with their kind", async ({
  signedIn,
}) => {
  await openMeeting(signedIn, READY);
  const media = signedIn.getByTestId("media-list");
  await expect(media.locator(".row")).toHaveCount(2);
  const audioRow = media.locator(".row", { hasText: "demo-1.wav" });
  const videoRow = media.locator(".row", { hasText: "demo-1-screen.mp4" });
  await expect(audioRow.locator(".meta")).toContainText("audio");
  // 30 s of 48 kHz mono PCM ≈ 2.8 MB — assert the shape, not a unit.
  await expect(audioRow.locator(".meta")).toContainText(/\d+(\.\d+)? (KB|MB)/);
  await expect(videoRow.locator(".meta")).toContainText("video");
  // The screen track's timeline offset is part of the contract and must be visible.
  await expect(videoRow.locator(".meta")).toContainText("starts at 0:02");
});

test("the shared player defaults to the AUDIO track and swaps to <video> for the screen track", async ({
  signedIn,
  consoleErrors,
}) => {
  await openMeeting(signedIn, READY);
  // Default: the audio track is the timeline anchor the transcript seeks against.
  await expect(signedIn.getByTestId("player-caption")).toContainText("(audio, 0:30)");
  await expect(signedIn.getByTestId("player-host").locator("video")).toHaveCount(0);

  const videoRow = signedIn.getByTestId("media-list").locator(".row", { hasText: "demo-1-screen.mp4" });
  await videoRow.getByTestId("load-playback").click();
  await expect(signedIn.getByTestId("player-caption")).toContainText("(video, 0:30)");

  // A screen recording must be WATCHABLE: the element becomes a real <video> with a duration.
  const element = signedIn.getByTestId("player-host").locator("video");
  await expect(element).toHaveCount(1);
  await expect.poll(async () => element.evaluate((node) => node.readyState), { timeout: 20_000 }).toBeGreaterThanOrEqual(1);
  const duration = await element.evaluate((node) => node.duration);
  expect(duration).toBeGreaterThan(20);
  expect(consoleErrors).toEqual([]);
});

test("a video track can be downloaded and deleted like any other recording", async ({ signedIn }) => {
  await openMeeting(signedIn, READY);
  const videoRow = signedIn.getByTestId("media-list").locator(".row", { hasText: "demo-1-screen.mp4" });
  const [download] = await Promise.all([
    signedIn.waitForEvent("download"),
    videoRow.getByTestId("download").click(),
  ]);
  expect(download.suggestedFilename()).toBe("demo-1-screen.mp4");
});

test("Re-run analysis posts the analyze step with force and reports the toast", async ({ signedIn }) => {
  await openMeeting(signedIn, READY);
  const [request] = await Promise.all([
    signedIn.waitForRequest((r) => r.url().includes("/process") && r.method() === "POST"),
    signedIn.getByRole("button", { name: "Re-run analysis" }).click(),
  ]);
  expect(request.url()).toContain("steps=analyze");
  expect(request.url()).toContain("force=true");
  await expect(signedIn.getByRole("status").filter({ hasText: "Analysis queued." })).toBeVisible();

  // Wait for the worker to settle, then prove the honest staleness rule: a re-run marks its
  // predecessor stale, but the kinds that now have a FRESH document must NOT be reported as out of
  // date (an unfiltered banner cried stale over current data — seen in a screenshot).
  await expect.poll(async () => signedIn.getByTestId("status").textContent(), { timeout: 30_000 }).toContain("Ready");
  await expect(signedIn.getByTestId("stale-banner")).toHaveCount(0);
  await signedIn.getByRole("tab", { name: "Analysis" }).click();
  await expect(signedIn.getByTestId("summary-bullets")).toBeVisible();

  // The list row applies the same rule — a superseded-but-replaced kind must not show as outdated.
  await signedIn.goto("/#/meetings");
  await expect(signedIn.locator(".row", { hasText: READY })).toBeVisible();
  await expect(signedIn.locator(".row", { hasText: READY }).getByTestId("stale-pill")).toHaveCount(0);
});

test("an unprocessed meeting says so instead of pretending", async ({ signedIn }) => {
  await openMeeting(signedIn, "Storage migration standup");
  await expect(signedIn.getByTestId("status")).toContainText("Not processed");
  await signedIn.getByRole("tab", { name: "Analysis" }).click();
  await expect(signedIn.getByTestId("no-analysis")).toContainText("No analysis is current");
});
