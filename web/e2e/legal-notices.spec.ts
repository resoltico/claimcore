import { expect, test } from "./fixtures";
import { noHorizontalOverflow } from "./layout-support";
import { gunzipSync } from "node:zlib";

test("offers public terms and published source from the signed-out screen @engine", async ({
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
    await page.addInitScript(() => {
      sessionStorage.setItem("claimcore.signedOut", "true");
    });
    await page.goto("/");
    await expect(page.getByRole("heading", { name: "You signed out" })).toBeVisible();
    await noHorizontalOverflow(page, "login");
    await page.getByRole("button", { name: "About ClaimCore", exact: true }).click();
    await expect(page.getByRole("link", { name: "Source for this build" })).toBeVisible();
    await noHorizontalOverflow(page, "narrow");
    const about = page.getByRole("dialog", { name: "About ClaimCore" });
    const readLink = async (name: string) => {
      const href = await about.getByRole("link", { name, exact: true }).getAttribute("href");
      if (href === null || !href.startsWith("/") || href.startsWith("//")) {
        throw new Error(`Expected a same-origin footer link for ${name}.`);
      }
      const response = await context.request.get(href);
      expect(response.status()).toBe(200);
      return response;
    };
    const license = await readLink("License");
    expect(license.status()).toBe(200);
    expect(await license.text()).toContain("GNU AFFERO GENERAL PUBLIC LICENSE");
    const notice = await readLink("Licensing and source details");
    expect(await notice.text()).toContain("AGPL");
    const notices = await readLink("Third-party notices");
    expect(await notices.text()).toContain("React");
    const commercial = await readLink("Commercial terms");
    expect(commercial.status()).toBe(200);
    expect((await commercial.text()).replace(/\s+/gu, " ")).toContain(
      "This file grants no exception, sublicense, warranty or waiver of AGPL obligations or third-party terms.",
    );
    const source = await readLink("Source for this build");
    expect(source.headers()["content-disposition"]).toBe('attachment; filename="SOURCE.tar.gz"');
    const archive = gunzipSync(await source.body());
    expect(archive.includes(Buffer.from("src/ClaimCore.Wire/LegalNotice.fs"))).toBe(true);
    expect(archive.includes(Buffer.from("web/package-lock.json"))).toBe(true);
  } finally {
    await context.close();
  }
});
