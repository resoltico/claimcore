import { readFile } from "node:fs/promises";
import { chromium, expect } from "@playwright/test";
import { consumerContractIdentity } from "../../contracts/client.ts";

const [origin, passwordFile] = process.argv.slice(2);
if (origin === undefined || passwordFile === undefined) {
  throw new Error("Supply the disposable production-template origin and synthetic password file.");
}

const browser = await chromium.launch();
try {
  // Infrastructure probes separately validate the synthetic certificate chain.
  const context = await browser.newContext({ ignoreHTTPSErrors: true });
  const page = await context.newPage();
  await page.goto(origin);
  await page.locator("#username").fill("owner");
  await page.locator("#password").fill((await readFile(passwordFile, "utf8")).trim());
  await page.locator("#kc-login").click();
  await expect(page.getByRole("heading", { name: "Cases", exact: true })).toBeVisible();

  const session = await context.request.get(`${origin}/api/session`, {
    headers: { "ClaimCore-Contract": consumerContractIdentity },
  });
  expect(session.status()).toBe(200);
  const body = await session.json();
  expect(body.outcome.data.authenticated).toBe(true);
  expect(body.outcome.data.contractIdentity).toBe(consumerContractIdentity);

  const lazy = page.waitForResponse(
    (response) => /\/assets\/.*RecoveryPanel.*\.js$/u.test(response.url()) && response.ok(),
  );
  await page.goto(`${origin}/recovery`);
  await lazy;
  await expect(page.getByRole("heading", { name: "Unconfirmed changes" })).toBeVisible();

  const viewer = await context.request.get(`${origin}/attachment-preview/viewer`);
  expect(viewer.status()).toBe(200);
  expect(viewer.headers()["content-security-policy"]).toContain("sandbox allow-scripts");
  expect(await viewer.text()).toContain("<canvas>");
} finally {
  await browser.close();
}
