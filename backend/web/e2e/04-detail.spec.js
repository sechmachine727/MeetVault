import { test, expect, openMeeting } from "./fixtures.js";

const READY = "Cloud console review";

test("opening a meeting shows the header, the facts and the player", async ({ signedIn, consoleErrors }) => {
  await openMeeting(signedIn, READY);
  await expect(signedIn.getByTestId("status")).toContainText("Ready");
  await expect(signedIn.getByTestId("player-bar")).toContainText("Playing:");

  const subtitle = signedIn.locator("p.sub").first();
  await expect(subtitle).toContainText("1 recording(s)");
  await expect(subtitle).toContainText("transcript version(s)");

  const facts = signedIn.getByTestId("overview");
  await expect(facts).toContainText("ready"); // the raw server status
  await expect(facts).toContainText("0:30");
  await expect(facts).toContainText("current"); // analysis is current
  // The header badge names exactly what is mock: the ACTIVE TRANSCRIPT's provider.
  await expect(signedIn.getByTestId("mock-badge")).toHaveText("Mock transcript");
  expect(consoleErrors).toEqual([]);
});

test("the overview lists the server recordings with size and a download action", async ({ signedIn }) => {
  await openMeeting(signedIn, READY);
  const media = signedIn.getByTestId("media-list");
  await expect(media.locator(".row")).toHaveCount(1);
  await expect(media).toContainText("demo-1.wav");
  await expect(media).toContainText("audio");
  await expect(media.locator(".meta")).toContainText("KB");
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
});

test("an unprocessed meeting says so instead of pretending", async ({ signedIn }) => {
  await openMeeting(signedIn, "Storage migration standup");
  await expect(signedIn.getByTestId("status")).toContainText("Not processed");
  await signedIn.getByRole("tab", { name: "Analysis" }).click();
  await expect(signedIn.getByTestId("no-analysis")).toContainText("No analysis is current");
});
