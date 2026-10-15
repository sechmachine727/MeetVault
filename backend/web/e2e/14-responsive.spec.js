import { test, expect, openMeeting } from "./fixtures.js";

// Runs in the `narrow` project (420x900) — see playwright.config.js.

const overflowOf = (page) =>
  page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);

test("the narrow layout does not overflow horizontally", async ({ signedIn, consoleErrors }) => {
  await expect(signedIn.getByRole("heading", { name: "Meetings in the cloud" })).toBeVisible();
  await expect(signedIn.locator(".row").first()).toBeVisible();
  expect(await overflowOf(signedIn)).toBeLessThanOrEqual(1);

  // The rail collapses to a top bar and stays reachable.
  await expect(signedIn.getByRole("link", { name: "Service" })).toBeVisible();
  expect(consoleErrors).toEqual([]);
});

test("a meeting detail stays usable and un-clipped at 420px", async ({ signedIn }) => {
  await openMeeting(signedIn, "Cloud console review");
  await signedIn.getByRole("tab", { name: "Transcript" }).click();
  await expect(signedIn.getByTestId("transcript").locator("table.segments tbody tr").first()).toBeVisible();
  expect(await overflowOf(signedIn)).toBeLessThanOrEqual(1);

  await signedIn.getByRole("tab", { name: "Analysis" }).click();
  await expect(signedIn.getByTestId("summary-bullets")).toBeVisible();
  expect(await overflowOf(signedIn)).toBeLessThanOrEqual(1);
});

test("the mobile header never breaks a word or a control", async ({ signedIn }) => {
  await expect(signedIn.locator(".row").first()).toBeVisible();
  const m = await signedIn.evaluate(() => {
    const mail = document.querySelector(".account .mail");
    const out = [...document.querySelectorAll("button")].find((b) => /sign out/i.test(b.textContent));
    const rail = document.querySelector(".rail");
    return {
      mailText: mail.textContent,
      mailLineBoxes: mail.getClientRects().length, // one rect per rendered line box
      mailClipped: mail.scrollWidth > mail.clientWidth + 1,
      signoutHeight: Math.round(out.getBoundingClientRect().height),
      signoutOneLine: Math.round(out.getBoundingClientRect().height) <= 40,
      railHeight: Math.round(rail.getBoundingClientRect().height),
      railScroll: rail.scrollWidth,
      railClient: rail.clientWidth,
    };
  });
  // A crushed flex child used to break the address mid-word ("demo@m / eetvault.lo / cal") and
  // split the Sign out control across three lines.
  expect(m.mailText).toBe("demo@meetvault.local");
  expect(m.mailLineBoxes).toBe(1);
  expect(m.mailClipped).toBe(false);
  expect(m.signoutOneLine, `sign out height ${m.signoutHeight}px`).toBe(true);
  expect(m.railScroll).toBeLessThanOrEqual(m.railClient + 1); // wraps, never scrolls sideways
  expect(m.railHeight).toBeLessThan(140); // and stays a header, not half the screen
});

test("the sign-in form fits a phone-width viewport", async ({ page }) => {
  await page.goto("/#/login");
  await expect(page.getByRole("button", { name: "Sign in" })).toBeVisible();
  const box = await page.getByRole("button", { name: "Sign in" }).boundingBox();
  expect(box.width).toBeLessThanOrEqual(420);
  // Here the rail is a TOP bar, so the card centres in the full width and in what is left below the
  // bar — and main must not claim another 100vh of its own (that scrolled the page by 250px).
  const m = await page.evaluate(() => {
    const rect = (sel) => {
      const node = document.querySelector(sel);
      return node ? node.getBoundingClientRect() : null;
    };
    const card = rect(".signin .card");
    const submit = rect(".signin button[type=submit]");
    const link = rect(".signin .form-actions a");
    const mid = (r) => r.top + r.height / 2;
    return {
      centerGap: Math.round(Math.abs(card.left + card.width / 2 - window.innerWidth / 2)),
      actionRowDelta: Math.round(Math.abs(mid(submit) - mid(link))),
      pageScroll: document.documentElement.scrollHeight - window.innerHeight,
    };
  });
  expect(m.centerGap).toBeLessThanOrEqual(1);
  expect(m.actionRowDelta).toBeLessThanOrEqual(2); // paired actions stay on one line, never wrapped
  expect(m.pageScroll).toBeLessThanOrEqual(1);
  expect(await overflowOf(page)).toBeLessThanOrEqual(1);
});

test("the sign-up form fits a phone-width viewport", async ({ page }) => {
  await page.goto("/#/signup");
  await expect(page.getByTestId("signup-submit")).toBeVisible();
  // Its own paired actions (Create account + Sign in instead) must stay inside the card, whether
  // they share the line or wrap.
  const actionsInsideCard = await page.evaluate(() => {
    const card = document.querySelector(".signin .card").getBoundingClientRect();
    return [...document.querySelectorAll(".form-actions > *")].every((n) => {
      const r = n.getBoundingClientRect();
      return r.left >= card.left - 1 && r.right <= card.right + 1;
    });
  });
  expect(actionsInsideCard).toBe(true);
  expect(await overflowOf(page)).toBeLessThanOrEqual(1);
});

// ---------------------------------------------------------------------------------------------
// The width scale. Four tiers: >1120 roomy, <=1120 compact (narrower rail, cap released),
// <=720 the rail becomes a top bar, <=520 one navigation line. Measured, because "responsive"
// is only real if the layout is checked at the widths between the two extremes too.
// ---------------------------------------------------------------------------------------------

test("the layout holds at every width on the scale, with no page-level overflow", async ({ signedIn }) => {
  const start = signedIn.viewportSize();
  const widths = [320, 360, 420, 480, 600, 719, 721, 768, 900, 1024, 1121, 1280];
  try {
    for (const width of widths) {
      await signedIn.setViewportSize({ width, height: 900 });
      await signedIn.getByRole("heading", { name: "Meetings in the cloud" }).waitFor();
      const m = await signedIn.evaluate(() => {
        const doc = document.documentElement;
        const rail = document.querySelector(".rail");
        const card = document.querySelector(".card");
        return {
          overflow: doc.scrollWidth - doc.clientWidth,
          railDir: getComputedStyle(rail).flexDirection,
          railW: Math.round(rail.getBoundingClientRect().width),
          cardGap: card ? Math.round(doc.clientWidth - card.getBoundingClientRect().right) : null,
        };
      });
      expect(m.overflow, `page overflow at ${width}px`).toBeLessThanOrEqual(1);
      expect(m.cardGap, `content gap at ${width}px`).toBeGreaterThanOrEqual(0);
      // The rail is a left column while it fits and a top bar once it does not: 720 stays a bar,
      // 721 is a column again.
      expect(m.railDir, `rail direction at ${width}px`).toBe(width <= 720 ? "row" : "column");
      // Below the top-bar switch the rail spans the page; above it, it must not grow with the
      // window (a fixed 216px column, 184px in the compact tier).
      if (width <= 720) expect(m.railW).toBe(width);
      else expect(m.railW).toBeLessThanOrEqual(216);
    }
  } finally {
    await signedIn.setViewportSize(start);
    await signedIn.getByRole("heading", { name: "Meetings in the cloud" }).waitFor();
  }
});

test("the detail view stays inside the viewport down to 360px", async ({ signedIn }) => {
  const start = signedIn.viewportSize();
  try {
    await signedIn.setViewportSize({ width: 360, height: 800 });
    await openMeeting(signedIn, "Cloud console review");
    await expect(signedIn.getByTestId("video-player")).toBeVisible();
    const m = await signedIn.evaluate(() => {
      const doc = document.documentElement;
      const vw = doc.clientWidth;
      const tabs = document.querySelector(".tabs");
      const video = document.querySelector('[data-testid="video-player"]');
      const toast = document.querySelector(".toast");
      return {
        overflow: doc.scrollWidth - vw,
        tabsScrolls: tabs.scrollWidth > tabs.clientWidth + 1,
        tabsInside: tabs.getBoundingClientRect().right <= vw + 1,
        videoW: Math.round(video.getBoundingClientRect().width),
        toastInside: !toast || toast.getBoundingClientRect().right <= vw + 1,
      };
    });
    expect(m.overflow).toBeLessThanOrEqual(1);
    // The seven tabs scroll sideways instead of stacking into three rows or escaping the page.
    expect(m.tabsScrolls).toBe(true);
    expect(m.tabsInside).toBe(true);
    // The player fits the column it was given.
    expect(m.videoW).toBeLessThanOrEqual(360);
    expect(m.toastInside).toBe(true);

    // The 4-column segment table gets its own scroll container rather than wrapping every cell.
    await signedIn.getByRole("tab", { name: "Transcript" }).click();
    await expect(signedIn.getByTestId("transcript").locator("table.segments tbody tr").first()).toBeVisible();
    const t = await signedIn.evaluate(() => {
      const doc = document.documentElement;
      const table = document.querySelector("table.segments");
      return {
        overflow: doc.scrollWidth - doc.clientWidth,
        tableScrolls: table.scrollWidth > table.clientWidth + 1,
        tableInside: table.getBoundingClientRect().right <= doc.clientWidth + 1,
      };
    });
    expect(t.overflow).toBeLessThanOrEqual(1);
    expect(t.tableScrolls).toBe(true);
    expect(t.tableInside).toBe(true);
  } finally {
    await signedIn.setViewportSize(start);
  }
});
