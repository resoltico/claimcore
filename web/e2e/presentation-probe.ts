import type { DisplayLocale } from "../src/presentation/preferences";
import { writeFile } from "node:fs/promises";
import { expect, type Page } from "@playwright/test";
import { consumerContractIdentity } from "@contracts/client";
import { actorContextInput } from "../tests/support/inputs";
import { workViewsFixture } from "../tests/support/view-counts.fixtures";
const presentationBasis = {
  observedAt: "2026-11-04T12:00:00.000000Z",
  businessDate: "2026-11-04",
  timeZone: "Etc/UTC",
};
export const captureCheckpoint = async (page: Page, path: string, resetScroll = true) => {
  if (resetScroll) {
    await page.evaluate(() => {
      window.scrollTo(0, 0);
    });
  }
  await page.evaluate(
    () =>
      new Promise<void>((resolve) => {
        requestAnimationFrame(() => {
          requestAnimationFrame(() => {
            resolve();
          });
        });
      }),
  );
  const camera = await page.context().newCDPSession(page);
  const result = await camera.send("Page.captureScreenshot", {
    format: "png",
    captureBeyondViewport: false,
  });
  await writeFile(path, Buffer.from(result.data, "base64"));
  await camera.detach();
};

type Violation = { directive: string; blocked: string };
type ProbeWindow = Window & { claimcorePresentationCsp?: Violation[] };
export const observe = async (
  page: Page,
  authenticated = false,
  expectedConsoleErrors: ReadonlyArray<RegExp> = [],
) => {
  const errors: string[] = [];
  const observedExpected = new Set<RegExp>();
  page.on("pageerror", (error) => {
    errors.push(error.message);
  });
  page.on("console", (message) => {
    if (message.type() === "error") {
      const allowed = expectedConsoleErrors.find((pattern) => pattern.test(message.text()));
      if (allowed === undefined) {
        errors.push(message.text());
      } else {
        observedExpected.add(allowed);
      }
    }
  });
  await page.addInitScript(() => {
    const scope = window as ProbeWindow;
    const violations: Violation[] = [];
    scope.claimcorePresentationCsp = violations;
    document.addEventListener("securitypolicyviolation", (event) => {
      violations.push({
        directive: event.effectiveDirective,
        blocked: event.blockedURI,
      });
    });
  });
  await page.route("**/api/session", (route) =>
    route.fulfill({
      json: {
        outcome: {
          kind: "SUCCEEDED",
          data: {
            authenticated,
            contractIdentity: consumerContractIdentity,
            antiforgeryToken: authenticated ? "synthetic-presentation" : null,
          },
        },
      },
    }),
  );
  if (authenticated) {
    const basis = {
      observedAt: "2026-11-04T00:00:00.000000Z",
      businessDate: "2026-11-04",
      timeZone: "Etc/UTC",
    };
    await page.route("**/api/work.views", (route) =>
      route.fulfill({
        json: { outcome: { kind: "SUCCEEDED", data: { ...workViewsFixture, basis } } },
      }),
    );
    await page.route("**/api/context", (route) =>
      route.fulfill({
        json: {
          outcome: {
            kind: "SUCCEEDED",
            data: {
              requester: actorContextInput({
                productVersion: "synthetic-presentation",
                observedAt: "2026-11-04T12:00:00.000000Z",
                businessDate: "2026-11-04",
                timeZone: "Etc/UTC",
                capabilities: ["READ_CASE", "READ_AUTHORITY"],
                views: ["CASES", "AUTHORITY", "OPERATIONS"],
                offers: [],
              }),
            },
          },
        },
      }),
    );
  }
  return async () => {
    expect(errors).toEqual([]);
    for (const pattern of expectedConsoleErrors) {
      expect(observedExpected.has(pattern)).toBe(true);
    }
    expect(
      await page.evaluate(() => {
        const violations = (window as ProbeWindow).claimcorePresentationCsp;
        if (violations === undefined) {
          throw new Error("E2E_CSP_OBSERVER_MISSING");
        }
        return violations;
      }),
    ).toEqual([]);
    expect(await page.locator("select").count()).toBe(0);
  };
};
export const seed = async (
  page: Page,
  options: {
    language?: "en" | "lv" | "ar";
    displayLocale?: DisplayLocale;
    mode?: "system" | "light" | "dark";
    accent?: "green" | "blue";
  } = {},
) => {
  await page.route("**/api/authority.requests", (route) =>
    route.fulfill({
      json: {
        outcome: {
          kind: "SUCCEEDED",
          data: { basis: presentationBasis, items: [], nextCursor: null },
        },
      },
    }),
  );
  await page.addInitScript((selected) => {
    if (localStorage.getItem("claimcore.presentation.v3") !== null) {
      return;
    }
    localStorage.setItem(
      "claimcore.presentation.v3",
      JSON.stringify({
        version: 3,
        language: selected.language ?? "en",
        displayLocale:
          selected.displayLocale ??
          ({ en: "en-GB", lv: "lv-LV", ar: "ar-EG" } as const)[selected.language ?? "en"],
        mode: selected.mode ?? "system",
        accent: selected.accent ?? "green",
      }),
    );
  }, options);
};
