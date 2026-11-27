import { randomUUID } from "node:crypto";
import { expect, test } from "./fixtures";
import { openCase } from "./case-workflow";
import { openAuthenticated } from "./session-helpers";

test.use({
  channel: "chromium",
  launchOptions: {
    ignoreDefaultArgs: [
      "--disable-back-forward-cache",
      "--enable-features=CDPScreenshotNewSurface",
    ],
    args: [
      "--enable-features=BackForwardCache,CacheControlNoStoreEnterBackForwardCache:level/restore-unless-cookie-change",
    ],
  },
});

test("a cached document resumes without its previous private facts @chromium", async ({
  page,
}, info) => {
  const cdp = await page.context().newCDPSession(page);
  const cacheMisses: string[] = [];
  await cdp.send("Page.enable");
  cdp.on("Page.backForwardCacheNotUsed", ({ notRestoredExplanations }) => {
    cacheMisses.push(...notRestoredExplanations.map(({ reason }) => reason));
  });
  const fact = `PRIVATE-HISTORY-RESTORE-${randomUUID().slice(0, 8)}`;
  await page.addInitScript((marker) => {
    window.addEventListener("pageshow", (event) => {
      if (event.persisted) {
        document.documentElement.dataset["restoredPrivateView"] =
          document.body.textContent.includes(marker) ? "exposed" : "masked";
      }
    });
  }, fact);
  await openAuthenticated(page);
  await openCase(page, fact);
  await expect(page.locator(".case-workspace")).toContainText(fact);
  const path = new URL(page.url()).pathname;

  await page.goto("about:blank");
  const fresh = page.waitForResponse(
    (response) => new URL(response.url()).pathname === "/api/case.get" && response.ok(),
  );
  const documentResponse = await page.goBack({ waitUntil: "commit" });
  await expect(page).toHaveURL((url) => url.pathname === path);
  try {
    if (documentResponse === null) {
      await expect(page.locator("html")).toHaveAttribute("data-restored-private-view", "masked");
    } else {
      // Chromium can decline a history-cache entry that received a protected no-store reply.
      await expect
        .poll(() => cacheMisses.some((reason) => reason.endsWith("CacheControlNoStoreResource")))
        .toBe(true);
      expect(documentResponse.headers()["cache-control"]).toContain("no-store");
      info.annotations.push({
        type: "history-cache-platform-limit",
        description: cacheMisses.join(", "),
      });
    }
  } finally {
    await info.attach("history-cache-diagnostics", {
      contentType: "application/json",
      body: JSON.stringify(cacheMisses),
    });
  }
  await fresh;
  await expect(page.locator(".case-workspace")).toContainText(fact);
});
