import { test, expect } from "./fixtures.js";

const READY = "Cloud console review";
const UPLOADED = "Storage migration standup";
const RUNNING = "Customer feedback sync";

test("the list shows every seeded meeting with its status and analysis marking", async ({
  signedIn,
  consoleErrors,
}) => {
  await expect(signedIn.locator(".row")).toHaveCount(3);
  for (const title of [READY, UPLOADED, RUNNING]) {
    await expect(signedIn.locator(".row", { hasText: title })).toBeVisible();
  }
  const readyRow = signedIn.locator(".row", { hasText: READY });
  await expect(readyRow.getByTestId("status")).toContainText("Ready");
  await expect(readyRow.getByTestId("analyzed-badge")).toBeVisible();
  // No mock badge on a list row: the list payload cannot prove what was mocked, and the aggregate
  // /status flag is true whenever ANY step ran mocked (it once told the owner a real analysis was
  // mock). The badges that state WHAT is mock live on the detail header and the Analysis tab.
  await expect(readyRow.getByTestId("mock-badge")).toHaveCount(0);

  const uploadedRow = signedIn.locator(".row", { hasText: UPLOADED });
  await expect(uploadedRow.getByTestId("status")).toContainText("Not processed");
  await expect(uploadedRow.getByTestId("analyzed-badge")).toHaveCount(0);

  const runningRow = signedIn.locator(".row", { hasText: RUNNING });
  await expect(runningRow).toHaveAttribute("data-kind", "processing");
  await expect(runningRow.getByTestId("status")).toContainText("Processing");
  expect(consoleErrors).toEqual([]);
});

test("the row meta carries the created date, the duration and the language", async ({ signedIn }) => {
  const meta = signedIn.locator(".row", { hasText: READY }).locator(".meta").first();
  const text = await meta.textContent();
  expect(text).toMatch(/\d{2} \w{3} \d{4}, \d{2}:\d{2}/);
  expect(text).toContain("0:30");
  expect(text).toContain("en");
});

test("search narrows the list and reports the page range", async ({ signedIn }) => {
  await expect(signedIn.getByTestId("page-summary")).toHaveText("1–3 of 3");
  await signedIn.getByLabel("Search meetings").fill("storage");
  await expect(signedIn.locator(".row")).toHaveCount(1);
  await expect(signedIn.locator(".row")).toContainText(UPLOADED);
  await expect(signedIn.getByTestId("page-summary")).toHaveText("1–1 of 1");
});

test("a search with no match shows the empty state, not a blank page", async ({ signedIn }) => {
  await signedIn.getByLabel("Search meetings").fill("no-such-meeting-title");
  await expect(signedIn.getByTestId("empty")).toContainText("No meetings match that filter.");
  await expect(signedIn.locator(".row")).toHaveCount(0);
});

test("the status filter narrows the list", async ({ signedIn }) => {
  await signedIn.getByLabel("Filter by status").selectOption("ready");
  await expect(signedIn.locator(".row")).toHaveCount(1);
  await expect(signedIn.locator(".row")).toContainText(READY);
  await signedIn.getByLabel("Filter by status").selectOption("");
  await expect(signedIn.locator(".row")).toHaveCount(3);
});

test("every filter option returns exactly the rows whose pill it names", async ({ signedIn }) => {
  const labels = await signedIn.getByLabel("Filter by status").locator("option").allTextContents();
  expect(labels).toEqual(["All statuses", "Ready", "Processing", "Not processed", "Failed"]);

  // The filter speaks the RAW meeting status while the pill shows a DERIVED one, so the two can
  // disagree: "Processing" used to return nothing while a row plainly read "Processing", and
  // "Uploaded" returned rows labelled "Not processed". Derive the expectation from what the user
  // sees, then require the filter to agree with it.
  const rows = signedIn.locator(".row");
  await expect(rows.first()).toBeVisible();
  const all = await rows.evaluateAll((nodes) =>
    nodes.map((r) => ({ id: r.dataset.row, pill: r.querySelector('[data-testid="status"]').textContent })),
  );
  expect(all).toHaveLength(3);

  for (const [value, pill] of [
    ["ready", "Ready"],
    ["processing", "Processing"],
    ["uploaded", "Not processed"],
  ]) {
    const expected = all.filter((r) => r.pill.startsWith(pill)).map((r) => r.id);
    expect(expected, `no seeded row shows "${pill}"`).not.toHaveLength(0);
    await signedIn.getByLabel("Filter by status").selectOption(value);
    await expect(rows).toHaveCount(expected.length); // auto-waits for the re-render
    const got = await rows.evaluateAll((nodes) => nodes.map((r) => r.dataset.row));
    expect(got.sort(), `filter ${value} returned the wrong rows`).toEqual([...expected].sort());
  }

  await signedIn.getByLabel("Filter by status").selectOption("");
  await expect(rows).toHaveCount(3);
});

test("a running job shows its step and elapsed, never a meaningless 0%", async ({ signedIn }) => {
  const pill = signedIn.locator('.row[data-kind="processing"] [data-testid="status"]');
  await expect(pill).toBeVisible();
  await expect(pill).toContainText("Processing");
  await expect(pill).toContainText("analyze running");
  await expect(pill).toHaveText(/\d+m \d+s/); // the elapsed timer is what proves it is alive
  // /status.progress is completed-steps/total-steps, so it sits at 0 for the whole first step
  // while the job itself is half done: a bare "0%" reads as "nothing is happening".
  expect(await pill.textContent()).not.toContain("0%");
});

test("server-side paging controls are wired (single page here, so both are disabled)", async ({
  signedIn,
}) => {
  await expect(signedIn.getByRole("button", { name: "Previous" })).toBeDisabled();
  await expect(signedIn.getByRole("button", { name: "Next" })).toBeDisabled();
});

test("Refresh re-reads the list from the server", async ({ signedIn }) => {
  const [request] = await Promise.all([
    signedIn.waitForRequest((r) => r.url().includes("/v1/meetings?") || r.url().endsWith("/v1/meetings")),
    signedIn.getByRole("button", { name: "Refresh" }).click(),
  ]);
  expect(request.method()).toBe("GET");
});

test("the remove dialog cancels safely", async ({ signedIn }) => {
  await signedIn
    .locator(".row", { hasText: READY })
    .getByRole("button", { name: "Remove from cloud" })
    .click();
  const dialog = signedIn.getByTestId("remove-dialog");
  await expect(dialog).toBeVisible();
  await expect(dialog).toContainText(READY);
  await dialog.getByRole("button", { name: "Cancel" }).click();
  await expect(dialog).toBeHidden();
  await expect(signedIn.locator(".row")).toHaveCount(3);
  // nothing was deleted on the server
  const token = await signedIn.evaluate(() => window.localStorage.getItem("mvweb.token"));
  const response = await signedIn.request.get("/v1/meetings", { headers: { Authorization: `Bearer ${token}` } });
  expect((await response.json()).total).toBe(3);
});
