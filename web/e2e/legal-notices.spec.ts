import { expect, test } from "@playwright/test";
import { noHorizontalOverflow } from "./layout-support";
import { gunzipSync } from "node:zlib";

test("offers public terms and the published build source before sign-in @engine", async ({
  browser,
  baseURL,
}) => {
  if (baseURL === undefined) {
    throw new Error("Published tests require a base URL.");
  }
  const context = await browser.newContext({ baseURL, ignoreHTTPSErrors: true });
  try {
    const page = await context.newPage();
    await page.setViewportSize({ width: 320, height: 720 });
    await page.goto("/");
    await noHorizontalOverflow(page, "login");
    await page.getByText("ClaimCore · AGPL-3.0-only · License and source", { exact: true }).click();
    await expect(page.getByRole("link", { name: "Source for this build" })).toBeVisible();
    await noHorizontalOverflow(page, "narrow");
    const license = await context.request.get("/LICENSE.txt");
    expect(license.status()).toBe(200);
    expect(await license.text()).toContain("GNU AFFERO GENERAL PUBLIC LICENSE");
    const commercial = await context.request.get("/LICENSE-COMMERCIAL.md");
    expect(commercial.status()).toBe(200);
    expect(await commercial.text()).toContain("This file grants no commercial exception");
    const source = await context.request.get("/SOURCE.tar.gz");
    expect(source.status()).toBe(200);
    const archive = gunzipSync(await source.body());
    expect(archive.includes(Buffer.from("src/ClaimCore.Wire/LegalNotice.fs"))).toBe(true);
    expect(archive.includes(Buffer.from("web/package-lock.json"))).toBe(true);
  } finally {
    await context.close();
  }
});
