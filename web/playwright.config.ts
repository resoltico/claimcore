import { defineConfig, devices } from "@playwright/test";
import { tmpdir } from "node:os";
import { isAbsolute, resolve } from "node:path";

const requiredBaseURL = process.env["CLAIMCORE_WEB_BASE_URL"] ?? "https://localhost:5443";

const localHttpsOrigin = (value: string): string => {
  const parsed = new URL(value);
  const port = Number.parseInt(parsed.port, 10);
  if (
    parsed.protocol !== "https:" ||
    parsed.hostname !== "localhost" ||
    !Number.isSafeInteger(port) ||
    port < 1 ||
    port > 65535 ||
    parsed.username.length > 0 ||
    parsed.password.length > 0 ||
    parsed.pathname !== "/" ||
    parsed.search.length > 0 ||
    parsed.hash.length > 0
  )
    throw new Error("Published browser tests must target one explicit local HTTPS origin.");
  return parsed.origin;
};

const baseURL = localHttpsOrigin(requiredBaseURL);
const reportScope = process.env["CLAIMCORE_WEB_E2E_ENGINE"] ?? "all";
const privateOutput =
  process.env["CLAIMCORE_WEB_E2E_PRIVATE_OUTPUT_DIR"] ??
  resolve(tmpdir(), `claimcore-playwright-private-${process.pid}`);

if (!["all", "chromium", "firefox", "webkit"].includes(reportScope)) {
  throw new Error("Published browser reports require one recognized engine scope.");
}
if (!isAbsolute(privateOutput)) {
  throw new Error("Published browser private output requires one absolute temporary directory.");
}

export default defineConfig({
  testDir: "./e2e",
  globalSetup: "./e2e/auth-setup.ts",
  fullyParallel: false,
  workers: 1,
  outputDir: privateOutput,
  reporter: [["./scripts/playwright-reporter.mjs"]],
  retries: 0,
  use: {
    baseURL,
    ignoreHTTPSErrors: true,
  },
  projects: [
    { name: "chromium", use: { ...devices["Desktop Chrome"] } },
    { name: "firefox", use: { ...devices["Desktop Firefox"] } },
    { name: "webkit", use: { ...devices["Desktop Safari"] } },
  ],
});
