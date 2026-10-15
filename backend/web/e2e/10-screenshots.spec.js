import fs from "node:fs";
import path from "node:path";

import { test, openMeeting, EMAIL, PASSWORD } from "./fixtures.js";

// Ordered screenshots for the owner's report. Every step asserts something visible first, so a
// blank or half-rendered page can never be captured as "evidence".
const DIR = path.resolve(process.env.MVB_REPORT_DIR || "../docs/web-report/screens");
fs.mkdirSync(DIR, { recursive: true });

const READY = "Cloud console review";

const shot = (page, name) => page.screenshot({ path: path.join(DIR, name), fullPage: true });

test("capture the console flow in order", async ({ page }) => {
  await page.goto("/#/login");
  await page.getByRole("heading", { name: "Sign in to the cloud" }).waitFor();
  await shot(page, "01-sign-in.png");

  await page.goto("/#/signup");
  await page.getByRole("heading", { name: "Create your account" }).waitFor();
  await shot(page, "01b-sign-up.png");
  await page.goto("/#/login");
  await page.getByRole("heading", { name: "Sign in to the cloud" }).waitFor();

  await page.getByLabel("Email").fill(EMAIL);
  await page.getByLabel("Password").fill(PASSWORD);
  await page.getByRole("button", { name: "Sign in" }).click();
  await page.getByRole("heading", { name: "Meetings in the cloud" }).waitFor();
  await page.locator(".row").first().waitFor();
  await shot(page, "02-meetings-list.png");

  // The remove dialog, cancelled — captured from the list where the destructive action lives.
  await page
    .locator(".row", { hasText: READY })
    .getByRole("button", { name: "Remove from cloud" })
    .click();
  await page.getByTestId("remove-dialog").waitFor();
  await shot(page, "03-remove-confirm.png");
  await page.getByTestId("remove-dialog").getByRole("button", { name: "Cancel" }).click();

  await openMeeting(page, READY);
  await page.getByTestId("overview").waitFor();
  await shot(page, "04-meeting-overview.png");

  // The screen-recording track: the shared player swaps to a real <video> element.
  const videoRow = page.getByTestId("media-list").locator(".row", { hasText: "-screen.mp4" });
  await videoRow.getByTestId("load-playback").click();
  await page.getByTestId("player-caption").filter({ hasText: "(video," }).waitFor();
  await page.getByTestId("player-host").locator("video").waitFor();
  await shot(page, "04b-screen-video.png");

  await page.getByRole("tab", { name: "Transcript" }).click();
  await page.getByTestId("transcript").locator("table.segments tbody tr").first().waitFor();
  await shot(page, "05-transcript.png");

  await page.getByRole("tab", { name: "Analysis" }).click();
  await page.getByTestId("summary-bullets").waitFor();
  await shot(page, "06-analysis.png");

  await page.getByRole("tab", { name: "Q&A" }).click();
  await page.getByTestId("qa-history").locator(".row").first().waitFor();
  await shot(page, "07-qa.png");

  await page.getByRole("tab", { name: "Speakers" }).click();
  await page.getByTestId("speakers").locator(".row").first().waitFor();
  await shot(page, "08-speakers.png");

  await page.getByRole("tab", { name: "Versions" }).click();
  await page.getByTestId("versions").locator(".row").first().waitFor();
  await shot(page, "09-versions.png");

  await page.getByRole("tab", { name: "Jobs" }).click();
  await page.getByTestId("jobs").locator(".row").first().waitFor();
  await shot(page, "10-jobs.png");

  await page.getByRole("link", { name: "Service" }).click();
  await page.getByTestId("providers").waitFor();
  await shot(page, "11-service.png");

  // A processing row (elapsed timer + progress + mock badge) as the list shows it.
  await page.goto("/#/meetings");
  await page.locator('.row[data-kind="processing"]').waitFor();
  await shot(page, "12-list-processing.png");

  // Narrow viewport: the responsive shell.
  await page.setViewportSize({ width: 420, height: 900 });
  await page.goto("/#/meetings");
  await page.locator(".row").first().waitFor();
  await shot(page, "13-narrow-list.png");
  await page.setViewportSize({ width: 1440, height: 900 });
});
