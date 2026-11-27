import { randomUUID } from "node:crypto";
import { uploadAttachment } from "./attachment-workflow";
import { redPage, syntheticPdf } from "./attachment-pdf-fixtures";
import { expect, test } from "./fixtures";
import { openCase } from "./case-workflow";
import { login, openAuthenticated, seedSignedOut } from "./session-helpers";

test("a delayed timer cannot keep private views visible past the foreground deadline @engine", async ({
  page,
}) => {
  test.setTimeout(90_000);
  const fact = `PRIVATE-OVERDUE-${randomUUID().slice(0, 8)}`;
  await page.addInitScript(() => {
    if (window !== window.top) {
      return;
    }
    const now = performance.now.bind(performance);
    let extra = 0;
    Object.defineProperty(performance, "now", { value: () => now() + extra });
    window.addEventListener("qualification-advance-monotonic", () => {
      extra = 16_000;
    });
    const schedule = window.setTimeout.bind(window);
    Object.defineProperty(window, "setTimeout", {
      configurable: true,
      value: (handler: TimerHandler, delay?: number, ...args: unknown[]) =>
        schedule(handler, delay === 15_000 ? 600_000 : delay, ...args),
    });
  });
  await openAuthenticated(page);
  await openCase(page, fact);
  const workspace = page.locator(".case-workspace");
  await expect(workspace).toContainText(fact);
  const attachments = page.getByRole("region", { name: "Attachments", exact: true });
  const name = `${fact}.pdf`;
  const { row } = await uploadAttachment(
    page,
    attachments,
    name,
    name,
    "application/pdf",
    syntheticPdf([redPage]),
  );
  await row.getByRole("button", { name: "Preview", exact: true }).click();
  await expect(row.getByRole("status")).toHaveText("Page 1 of 1");

  const history = page.locator(".case-timeline-panel");
  await history.getByRole("button", { name: "View details", exact: true }).last().click();
  const dialog = page.getByRole("dialog", { name: "View details", exact: true });
  await expect(dialog).toContainText(fact);
  await expect(workspace.locator("iframe")).toHaveCount(1);

  const prepared = Promise.withResolvers<boolean>();
  const release = Promise.withResolvers<void>();
  await page.route(
    "**/api/case.get",
    async (route) => {
      const reply = await route.fetch();
      prepared.resolve(reply.ok() && (await reply.text()).includes(fact));
      await release.promise;
      await route.fulfill({ response: reply });
    },
    { times: 1 },
  );

  try {
    const afterFrames = await page.evaluate(async (marker) => {
      const start = performance.now();
      window.dispatchEvent(new Event("qualification-advance-monotonic"));
      await new Promise<void>((resolve) => {
        requestAnimationFrame(() => {
          requestAnimationFrame(() => {
            resolve();
          });
        });
      });
      return {
        elapsed: performance.now() - start,
        visible: document.visibilityState === "visible",
        staleFact: document.body.textContent.includes(marker),
        retainedPreview: document.querySelector(".case-workspace iframe") !== null,
      };
    }, fact);
    expect(afterFrames.elapsed).toBeGreaterThan(15_000);
    expect(afterFrames.visible).toBe(true);
    expect(afterFrames.staleFact).toBe(false);
    expect(afterFrames.retainedPreview).toBe(false);
    expect(await prepared.promise).toBe(true);
    await expect(page.locator("body")).not.toContainText(fact);
    release.resolve();
    await expect(workspace).toContainText(fact);
    await expect(dialog).toHaveCount(0);
    await expect(workspace.locator("iframe")).toHaveCount(0);
  } finally {
    release.resolve();
  }
});

test("private facts expire on the monotonic clock and await a new admission @engine", async ({
  page,
}) => {
  test.setTimeout(90_000);
  await page.clock.install();
  await openAuthenticated(page);
  const fact = `PRIVATE-LIFETIME-${randomUUID().slice(0, 8)}`;
  await openCase(page, fact);
  const workspace = page.locator(".case-workspace");
  await expect(workspace).toContainText(fact);

  const parked = Promise.withResolvers<void>();
  const permit = Promise.withResolvers<void>();
  await page.route("**/api/case.get", async (route) => {
    parked.resolve();
    await permit.promise;
    await route.continue().catch(() => undefined);
  });

  try {
    // Changing the wall clock cannot renew a foreground display generation.
    const wall = await page.evaluate(() => Date.now());
    await page.clock.setSystemTime(wall - 6 * 60 * 60 * 1000);
    await page.clock.fastForward(16_000);
    await parked.promise;
    await expect(workspace).not.toContainText(fact);

    permit.resolve();
    await expect(workspace).toContainText(fact);

    await page.context().setOffline(true);
    await page.evaluate(() => window.dispatchEvent(new Event("offline")));
    await expect(workspace).not.toContainText(fact);
    await page.context().setOffline(false);
    await page.evaluate(() => window.dispatchEvent(new Event("online")));
    await expect(workspace).toContainText(fact);
  } finally {
    permit.resolve();
    await page.context().setOffline(false);
  }
});

for (const initial of ["hidden", "offline"] as const) {
  test(`a protected workspace starts masked when ${initial} @engine`, async ({ page }) => {
    await openAuthenticated(page);
    const fact = `PRIVATE-INITIAL-${randomUUID().slice(0, 8)}`;
    await openCase(page, fact);
    await expect(page.locator(".case-workspace")).toContainText(fact);

    await page.addInitScript((state) => {
      let inactive = true;
      Object.defineProperty(document, "visibilityState", {
        configurable: true,
        get: () => (inactive && state === "hidden" ? "hidden" : "visible"),
      });
      Object.defineProperty(navigator, "onLine", {
        configurable: true,
        get: () => !(inactive && state === "offline"),
      });
      window.addEventListener("qualification-activate", () => {
        inactive = false;
        if (state === "hidden") {
          document.dispatchEvent(new Event("visibilitychange"));
        } else {
          window.dispatchEvent(new Event("online"));
        }
      });
    }, initial);

    let requests = 0;
    await page.route("**/api/case.get", async (route) => {
      requests += 1;
      await route.continue();
    });
    const session = page.waitForResponse(
      (reply) => new URL(reply.url()).pathname === "/api/session",
    );
    await page.reload();
    expect((await session).ok()).toBe(true);
    await expect(page.locator(".workspace-shell")).toBeVisible();
    expect(requests).toBe(0);
    await expect(page.locator("body")).not.toContainText(fact);

    await page.evaluate(() => window.dispatchEvent(new Event("qualification-activate")));
    await expect(page.locator(".case-workspace")).toContainText(fact);
    expect(requests).toBeGreaterThan(0);
  });
}

test("activation discards an authorized case reply prepared before suspension @engine", async ({
  page,
}) => {
  await openAuthenticated(page);
  const fact = `PRIVATE-BEFORE-ACTIVATION-${randomUUID().slice(0, 8)}`;
  await openCase(page, fact);
  await expect(page.locator(".case-workspace")).toContainText(fact);
  const oldPrepared = Promise.withResolvers<void>();
  const sendOld = Promise.withResolvers<void>();
  const oldSettled = Promise.withResolvers<void>();
  const freshPrepared = Promise.withResolvers<void>();
  const sendFresh = Promise.withResolvers<void>();
  let reads = 0;
  await page.route("**/api/case.get", async (route) => {
    const first = reads++ === 0;
    try {
      const reply = await route.fetch();
      expect(reply.ok()).toBe(true);
      (first ? oldPrepared : freshPrepared).resolve();
      await (first ? sendOld : sendFresh).promise;
      await route.fulfill({ response: reply }).catch(() => undefined);
    } finally {
      if (first) {
        oldSettled.resolve();
      }
    }
  });

  try {
    await page.evaluate(() => window.dispatchEvent(new Event("focus")));
    await oldPrepared.promise;
    await page.evaluate(() => {
      window.dispatchEvent(new PageTransitionEvent("pagehide", { persisted: true }));
      window.dispatchEvent(new Event("focus"));
      window.dispatchEvent(new Event("focus"));
    });
    await expect(page.locator("body")).not.toContainText(fact);
    expect(reads).toBe(1);
    await page.evaluate(() =>
      window.dispatchEvent(new PageTransitionEvent("pageshow", { persisted: true })),
    );
    await freshPrepared.promise;
    sendOld.resolve();
    await oldSettled.promise;
    await expect(page.locator("body")).not.toContainText(fact);
    sendFresh.resolve();
    await expect(page.locator(".case-workspace")).toContainText(fact);
  } finally {
    sendOld.resolve();
    sendFresh.resolve();
  }
});

test("replacing the browser credential leaves an old account's held result masked @engine", async ({
  page,
  browser,
  baseURL,
}) => {
  test.setTimeout(160_000);
  const replacement = await browser.newContext({
    baseURL: baseURL!,
    ignoreHTTPSErrors: true,
    storageState: { cookies: [], origins: [] },
  });
  const ready = Promise.withResolvers<void>();
  const release = Promise.withResolvers<void>();
  const settled = Promise.withResolvers<void>();
  try {
    const outsider = await replacement.newPage();
    await seedSignedOut(outsider);
    await outsider.goto("/", { waitUntil: "commit" });
    await login(outsider, "Request access", "Sign in", "synthetic-outsider");

    await openAuthenticated(page);
    const fact = `PRIOR-CREDENTIAL-${randomUUID().slice(0, 8)}`;
    await openCase(page, fact);
    await expect(page.locator(".case-workspace")).toContainText(fact);

    await page.route(
      "**/api/case.get",
      async (route) => {
        try {
          const authorized = await route.fetch();
          expect(authorized.status()).toBe(200);
          expect(await authorized.text()).toContain(fact);
          ready.resolve();
          await release.promise;
          await route.fulfill({ response: authorized }).catch(() => undefined);
        } finally {
          settled.resolve();
        }
      },
      { times: 1 },
    );
    await page.evaluate(() => window.dispatchEvent(new Event("focus")));
    await ready.promise;
    await page.context().clearCookies();
    await page.context().addCookies(await replacement.cookies());
    const context = page.waitForResponse((response) => response.url().endsWith("/api/context"));
    await page.evaluate(() => window.dispatchEvent(new Event("focus")));
    await context;
    release.resolve();
    await settled.promise;
    await expect(page.locator("body")).not.toContainText(fact);
    await page.reload();
    await expect(page.getByRole("heading", { name: "Request access" })).toBeVisible();
    await expect(page.locator("body")).not.toContainText(fact);
  } finally {
    release.resolve();
    await replacement.close();
  }
});
