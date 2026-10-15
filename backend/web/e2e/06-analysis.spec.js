import { test, expect, openMeeting } from "./fixtures.js";

const READY = "Cloud console review";

async function openAnalysis(page) {
  await openMeeting(page, READY);
  await page.getByRole("tab", { name: "Analysis" }).click();
  await expect(page.getByTestId("analysis")).toBeVisible();
}

test("the analysis tab shows the summary, agenda, sections, decisions and action items", async ({
  signedIn,
  consoleErrors,
}) => {
  await openAnalysis(signedIn);
  const analysis = signedIn.getByTestId("analysis");
  await expect(analysis).toContainText("Summary");
  await expect(analysis).toContainText("Analyzed by mock-analyzer");
  await expect(signedIn.getByTestId("summary-bullets").locator("li")).toHaveCount(2);

  await expect(signedIn.getByTestId("agenda").locator(".row")).toHaveCount(3);
  await expect(signedIn.getByTestId("agenda")).toContainText("[mock] Opening");

  const sections = signedIn.getByTestId("sections");
  expect(await sections.locator(".row").count()).toBeGreaterThanOrEqual(1);
  await expect(sections).toContainText("[mock]");

  await expect(signedIn.getByTestId("decision").locator(".row")).toHaveCount(1);
  await expect(signedIn.getByTestId("decision")).toContainText("[mock] decision");
  await expect(signedIn.getByTestId("action").locator(".row")).toHaveCount(1);
  await expect(signedIn.getByTestId("action")).toContainText("Speaker 2");
  expect(consoleErrors).toEqual([]);
});

test("mock analysis is never disguised: the badge and the [mock] markers agree", async ({ signedIn }) => {
  await openAnalysis(signedIn);
  await expect(signedIn.getByTestId("analysis").getByTestId("mock-analysis-badge")).toHaveText("Mock analysis");
  await expect(signedIn.getByTestId("analysis")).toContainText("[mock] Summary");
});

test("a section timestamp seeks the recording", async ({ signedIn }) => {
  await openAnalysis(signedIn);
  const player = signedIn.locator('[data-testid="player"]');
  await signedIn.getByTestId("sections").getByTestId("seek-section").first().click();
  await expect.poll(async () => player.evaluate((element) => element.currentTime), { timeout: 5000 }).toBeGreaterThan(0);
});

test("agenda items carry their topic and a seekable start time", async ({ signedIn }) => {
  await openAnalysis(signedIn);
  const firstAgendaRow = signedIn.getByTestId("agenda").locator(".row").first();
  await expect(firstAgendaRow.locator(".meta")).toHaveText("intro");
  await expect(firstAgendaRow.getByTestId("seek-agenda")).toHaveText(/\d+:\d{2}/);
});
