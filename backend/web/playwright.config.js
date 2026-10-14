import { defineConfig } from "@playwright/test";

// The demo stack (api + worker + seeded data) is started by web/run-demo-stack.sh — the suite
// never manages it, so a missing/incorrectly-seeded backend fails loudly instead of being hidden.
export default defineConfig({
  testDir: "./e2e",
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  workers: 1, // the seeded dataset is shared; spec files are numbered for a safe mutation order
  reporter: [["list"], ["html", { outputFolder: "playwright-report", open: "never" }]],
  use: {
    baseURL: process.env.MVB_DEMO_URL || "http://127.0.0.1:8099",
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
    viewport: { width: 1440, height: 900 },
  },
  projects: [
    // narrow FIRST: the responsive spec is read-only, so it runs against the pristine seed before
    // the mutating specs (transcript activation makes the analysis stale, and deleting a version
    // cascades the analysis away).
    { name: "narrow", testMatch: /responsive\.spec\.js$/, use: { viewport: { width: 420, height: 900 } } },
    { name: "desktop", testIgnore: /responsive\.spec\.js$/ },
  ],
});
