import { test, expect, openMeeting } from "./fixtures.js";

const READY = "Cloud console review";
// A helper that reports every interactive control the browser could not name — screen readers and
// Playwright both rely on this.
const unnamedControls = (page) =>
  page.evaluate(() => {
    const offenders = [];
    for (const node of document.querySelectorAll("button, a[href], input, select, textarea")) {
      if (node.type === "hidden") continue;
      const name =
        (node.innerText || "").trim() ||
        node.getAttribute("aria-label") ||
        node.getAttribute("title") ||
        (node.labels && node.labels.length ? node.labels[0].innerText.trim() : "") ||
        "";
      if (!name) offenders.push(node.outerHTML.slice(0, 90));
    }
    return offenders;
  });

// A text node whose whole content is "null"/"undefined" is always a bug: it means a conditional
// child reached the raw DOM `append()`, which stringifies instead of skipping it.
const strayNulls = (page) =>
  page.evaluate(() => {
    const out = [];
    const walk = (node) => {
      for (const child of node.childNodes) {
        if (child.nodeType === 3) {
          const text = child.textContent.trim();
          if (text === "null" || text === "undefined") out.push(text);
        } else if (child.nodeType === 1 && child.tagName !== "SCRIPT") {
          walk(child);
        }
      }
    };
    walk(document.body);
    return out;
  });

test("the meetings list has a name for every control", async ({ signedIn }) => {
  expect(await unnamedControls(signedIn)).toEqual([]);
});

test("no surface renders a stray null or undefined text node", async ({ signedIn }) => {
  await expect(signedIn.locator(".row").first()).toBeVisible();
  expect(await strayNulls(signedIn)).toEqual([]);
  await openMeeting(signedIn, READY);
  for (const tab of ["Overview", "Transcript", "Analysis", "Q&A", "Speakers", "Versions", "Jobs"]) {
    await signedIn.getByRole("tab", { name: tab }).click();
    await signedIn.waitForTimeout(400);
    expect(await strayNulls(signedIn), `tab ${tab}`).toEqual([]);
  }
});

test("every tab of the detail view has a name for every control", async ({ signedIn }) => {
  await openMeeting(signedIn, READY);
  for (const tab of ["Overview", "Transcript", "Analysis", "Q&A", "Speakers", "Versions", "Jobs"]) {
    await signedIn.getByRole("tab", { name: tab }).click();
    await signedIn.waitForTimeout(400); // let the panel paint before auditing it
    expect(await unnamedControls(signedIn), `tab ${tab}`).toEqual([]);
  }
});

test("the tab strip and the status toast expose their roles", async ({ signedIn }) => {
  await openMeeting(signedIn, READY);
  const tablist = signedIn.getByRole("tablist");
  await expect(tablist).toBeVisible();
  await expect(tablist.getByRole("tab")).toHaveCount(7);
  await expect(signedIn.getByRole("tab", { name: "Overview" })).toHaveAttribute("aria-selected", "true");
  await expect(signedIn.locator("#toast")).toHaveAttribute("role", "status");
});

test("the sign-in form is labelled", async ({ page }) => {
  await page.goto("/#/login");
  await expect(page.getByLabel("Email")).toBeVisible();
  await expect(page.getByLabel("Password")).toBeVisible();
  expect(await unnamedControls(page)).toEqual([]);
});
