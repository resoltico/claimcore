import { chmod, mkdir } from "node:fs/promises";
import { resolve } from "node:path";

import { chromium, firefox, webkit } from "@playwright/test";
import { login } from "./session-helpers";
import { grantSyntheticCasework } from "./authority-setup";

const required = (name: string): string => {
  const value = process.env[name];
  if (value === undefined || value === "") {
    throw new Error(`Missing published browser ${name}.`);
  }
  return value;
};

const browserFor = (engine: string) => {
  if (engine === "chromium") {
    return chromium;
  }
  if (engine === "firefox") {
    return firefox;
  }
  if (engine === "webkit") {
    return webkit;
  }
  throw new Error("Published browser setup requires one engine.");
};

export default async function setup(): Promise<void> {
  const engine = required("CLAIMCORE_WEB_E2E_ENGINE");
  const output = required("CLAIMCORE_WEB_E2E_PRIVATE_OUTPUT_DIR");
  required("CLAIMCORE_TEST_OIDC_CREDENTIALS");
  const baseURL = required("CLAIMCORE_WEB_BASE_URL");
  await mkdir(output, { recursive: true, mode: 0o700 });
  const browser = await browserFor(engine).launch();
  try {
    const context = await browser.newContext({ ignoreHTTPSErrors: true });
    const page = await context.newPage();
    await page.goto(baseURL);
    await login(page);
    await grantSyntheticCasework(page);
    await page.getByRole("heading", { name: "Cases" }).waitFor();
    const state = resolve(output, "authenticated-state.json");
    await context.storageState({ path: state });
    await chmod(state, 0o600);
    await context.close();
  } finally {
    await browser.close();
  }
}
