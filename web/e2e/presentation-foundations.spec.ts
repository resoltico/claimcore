import { isRequest } from "../src/protocol/client";
import { writeFile } from "node:fs/promises";
import { casePath } from "../src/routing/routes";
import { timelineEntry, timelinePage } from "../tests/support/timeline.fixtures";
import {
  caseFields,
  operationId as entryOperationId,
} from "../tests/support/service-foundation.fixtures";
import { caseViewsFixture, reminderViewsFixture } from "../tests/support/view-counts.fixtures";
import { expect, test, type Page } from "@playwright/test";
import { consumerContractIdentity, type RequesterState } from "@contracts/client";
import {
  actorContextInput,
  caseSummaryInput,
  actorRefInput,
  caseViewInput,
} from "../tests/support/inputs";
import { contrastRatio } from "../src/theme/contrast";
import { chooseOption } from "./workspace-navigation";
import { expectAccessible } from "./session-helpers";

type Violation = { directive: string; blocked: string };
type ProbeWindow = Window & { claimcorePresentationCsp?: Violation[] };
const observe = async (
  page: Page,
  authenticated = false,
  expectedConsoleErrors: ReadonlyArray<RegExp> = [],
) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => {
    errors.push(error.message);
  });
  page.on("console", (message) => {
    if (message.type() === "error") {
      errors.push(message.text());
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
    await page.route("**/api/case.views", (route) =>
      route.fulfill({
        json: { outcome: { kind: "SUCCEEDED", data: { ...caseViewsFixture, basis } } },
      }),
    );
    await page.route("**/api/reminder.views", (route) =>
      route.fulfill({
        json: { outcome: { kind: "SUCCEEDED", data: { ...reminderViewsFixture, basis } } },
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
    expect(errors).toEqual(expectedConsoleErrors.map((pattern) => expect.stringMatching(pattern)));
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
const seed = async (
  page: Page,
  options: {
    language?: "en" | "lv" | "ar";
    mode?: "system" | "light" | "dark";
    accent?: "green" | "blue";
  } = {},
) => {
  await page.addInitScript((selected) => {
    if (localStorage.getItem("claimcore.presentation.v3") !== null) {
      return;
    }
    localStorage.setItem(
      "claimcore.presentation.v3",
      JSON.stringify({
        version: 3,
        language: selected.language ?? "en",
        displayLocale: ({ en: "en-GB", lv: "lv-LV", ar: "ar-EG" } as const)[
          selected.language ?? "en"
        ],
        mode: selected.mode ?? "system",
        accent: selected.accent ?? "green",
      }),
    );
  }, options);
};
const signInSettings = async (page: Page) => {
  await page.goto("/", { waitUntil: "commit" });
  await page.locator(".login-footer button").click();
  await expect(page.getByRole("dialog")).toBeVisible();
};
const hexColor = (rgb: string, background?: string): string => {
  const components = /^rgba?\((\d+), (\d+), (\d+)(?:, ([\d.]+))?\)$/u.exec(rgb);
  if (components === null) {
    throw new Error("E2E_UNEXPECTED_COMPUTED_COLOR");
  }
  const alpha = Number(components[4] ?? 1);
  const channels = components.slice(1, 4).map(Number);
  if (alpha < 1) {
    if (background === undefined) {
      throw new Error("E2E_OPAQUE_BACKGROUND_REQUIRED");
    }
    const base = hexColor(background);
    channels.forEach((channel, index) => {
      channels[index] = Math.round(
        channel * alpha +
          Number.parseInt(base.slice(1 + index * 2, 3 + index * 2), 16) * (1 - alpha),
      );
    });
  }
  return `#${channels.map((channel) => channel.toString(16).padStart(2, "0")).join("")}`;
};

test("keeps presentation controls in Settings and applies persisted choices without API work", async ({
  page,
}) => {
  const clean = await observe(page);
  await seed(page);
  const requests: string[] = [];
  page.on("request", (request) => {
    const path = new URL(request.url()).pathname;
    if (path.startsWith("/api/") && path !== "/api/session") {
      requests.push(path);
    }
  });
  await page.goto("/", { waitUntil: "commit" });
  await expect(page.locator('.login-card :is(input[type="radio"], [role="combobox"])')).toHaveCount(
    0,
  );
  const trigger = page.getByRole("button", {
    name: "Language & appearance · English",
    exact: true,
  });
  await trigger.click();
  const panel = page.locator(".settings-panel");
  await expect(panel.locator('input[type="radio"]')).toHaveCount(8);
  await expect(panel.locator('[aria-live="polite"]')).toBeEmpty();
  await chooseOption(
    page,
    panel.getByRole("combobox", { name: "Date and number format" }),
    "en-US",
  );
  await panel.getByRole("radio", { name: "Blue", exact: true }).check();
  await panel.getByRole("radio", { name: "Dark", exact: true }).check();
  await expect(page.locator("html")).toHaveAttribute("data-accent", "blue");
  await expect(page.locator("html")).toHaveAttribute("data-mode", "dark");
  await page.keyboard.press("Escape");
  await expect(trigger).toBeFocused();
  await page.reload();
  await page.locator(".login-footer button").click();
  await expect(page.locator('.settings-panel [role="combobox"]')).toHaveAttribute("value", "en-US");
  await expect(page.locator('.settings-panel input[value="blue"]')).toBeChecked();
  await expect(page.locator('.settings-panel input[value="dark"]')).toBeChecked();
  expect(requests).toEqual([]);
  await expectAccessible(page);
  await clean();
});

test("select-only listbox supports keyboard search, commit, Escape and focus return under CSP", async ({
  page,
}) => {
  const clean = await observe(page);
  await seed(page);
  await signInSettings(page);
  const picker = page.getByRole("combobox", { name: "Date and number format" });
  await picker.focus();
  await page.keyboard.press("End");
  await expect(page.getByRole("listbox")).toBeVisible();
  await expect(page.getByRole("option").last()).toBeFocused();
  await page.keyboard.press("Escape");
  await expect(picker).toBeFocused();
  await page.keyboard.press("ArrowDown");
  await expect(page.getByRole("listbox")).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(picker).toBeFocused();
  await page.keyboard.press("Home");
  await expect(page.getByRole("listbox")).toBeVisible();
  await expect(page.getByRole("option").first()).toBeFocused();
  await page.keyboard.type("Deutsch");
  await expect(page.getByRole("option", { name: "Deutsch — Deutschland" })).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(picker).toHaveAttribute("value", "de-DE");
  await expect(picker).toBeFocused();
  await page.keyboard.press("ArrowDown");
  await page.keyboard.press("Home");
  await page.keyboard.press("Escape");
  await expect(picker).toHaveAttribute("value", "de-DE");
  await expect(picker).toBeFocused();
  await clean();
});

test("programmatic heading focus stays quiet after keyboard use and controls retain visible focus", async ({
  page,
}) => {
  const clean = await observe(page, true);
  await seed(page);
  await page.goto("/settings", { waitUntil: "commit" });
  const heading = page.getByRole("heading", { name: "Settings", exact: true });
  await expect(heading).toBeFocused();
  await page.keyboard.press("Tab");
  await heading.focus();
  await expect(heading).toHaveCSS("outline-style", "none");
  const picker = page.getByRole("combobox", { name: "Date and number format" });
  await picker.focus();
  await page.keyboard.press("ArrowDown");
  await page.keyboard.press("Escape");
  await expect(picker).toHaveCSS("outline-width", "2px");
  await expect(picker).toHaveCSS("outline-offset", "2px");
  await clean();
});

test("Settings reflows at 320px and 200% text without overflowing accent cards", async ({
  page,
}) => {
  const clean = await observe(page, true);
  await seed(page, { language: "lv", mode: "dark", accent: "blue" });
  await page.setViewportSize({ width: 320, height: 900 });
  await page.goto("/settings", { waitUntil: "commit" });
  await expect(page.locator(".settings-panel")).toBeVisible();
  await page.evaluate(() => {
    document.documentElement.style.fontSize = "200%";
  });
  await expect(page.locator("html")).toHaveCSS("font-size", "32px");
  const overflow = await page.evaluate(() => {
    const cards = [...document.querySelectorAll<HTMLElement>(".accent-card")];
    return {
      document: document.documentElement.scrollWidth > document.documentElement.clientWidth,
      cards: cards.map((card) => {
        const bound = card.getBoundingClientRect();
        return [...card.querySelectorAll<HTMLElement>("*")].some((child) => {
          const rect = child.getBoundingClientRect();
          return rect.left < bound.left - 1 || rect.right > bound.right + 1;
        });
      }),
    };
  });
  expect(overflow).toEqual({ document: false, cards: [false, false] });
  await clean();
});

for (const language of ["en", "lv", "ar"] as const) {
  test(`sign-in Settings dialog contains accent contents at 320px and 200% text in ${language}`, async ({
    page,
  }) => {
    const clean = await observe(page);
    await seed(page, { language });
    await page.setViewportSize({ width: 320, height: 900 });
    await signInSettings(page);
    await page.evaluate(() => {
      document.documentElement.style.fontSize = "200%";
    });
    await page.locator(".settings-accents").scrollIntoViewIfNeeded();
    const bounds = await page.locator(".accent-card").evaluateAll((cards) =>
      cards.map((card) => {
        const bound = card.getBoundingClientRect();
        return [...card.querySelectorAll("*")].every((child) => {
          const rect = child.getBoundingClientRect();
          return rect.left >= bound.left - 1 && rect.right <= bound.right + 1;
        });
      }),
    );
    expect(bounds).toEqual([true, true]);
    await clean();
  });
}

for (const language of ["en", "ar"] as const) {
  for (const width of [320, 800, 1440]) {
    test(`shell uses the adopted navigation without overflow at ${width}px in ${language}`, async ({
      page,
    }) => {
      const clean = await observe(page, true);
      await seed(page, { language });
      await page.setViewportSize({ width, height: 900 });
      await page.goto("/settings", { waitUntil: "commit" });
      await expect(page.locator(".settings-panel")).toBeVisible();
      if (width === 320) {
        await page.evaluate(() => {
          document.documentElement.style.fontSize = "200%";
        });
      }
      const mobile = width === 320;
      await expect(page.locator(".workspace-mobile-tabs")).toHaveCount(mobile ? 1 : 0);
      await expect(page.locator(".workspace-sidebar nav")).toHaveCount(mobile ? 0 : 1);
      await expect(page.locator('nav a[href="/recovery"]')).toHaveCount(0);
      await expect(page.locator(".shell-business-date")).toHaveCount(1);
      const overflow = await page.evaluate(() => ({
        document: document.documentElement.scrollWidth > document.documentElement.clientWidth,
        main:
          document.querySelector("main")!.getBoundingClientRect().right >
          document.documentElement.clientWidth + 1,
      }));
      expect(overflow).toEqual({ document: false, main: false });
      await expectAccessible(page);
      await clean();
    });
  }
}

test("read admission retry stays non-blocking and honors the server delay under CSP", async ({
  page,
  browserName,
}) => {
  // Chromium/WebKit report the injected HTTP error in console; Firefox does not.
  const expectedConsole =
    browserName === "firefox" ? [] : [/server responded with a status of 429/u];
  const clean = await observe(page, true, expectedConsole);
  await seed(page);
  const dispatches: number[] = [];
  await page.route("**/api/context", (route) => {
    dispatches.push(Date.now());
    if (dispatches.length === 1) {
      return route.fulfill({
        status: 429,
        headers: { "Retry-After": "1" },
        json: { hostFailure: { code: "RATE_LIMITED", phase: "NOT_STARTED" } },
      });
    }
    return route.fulfill({
      json: {
        outcome: {
          kind: "SUCCEEDED",
          data: {
            requester: actorContextInput({
              productVersion: "synthetic-presentation",
              businessDate: "2026-11-04",
              timeZone: "Etc/UTC",
              capabilities: [],
              views: [],
              offers: [],
            }),
          },
        },
      },
    });
  });
  await page.goto("/settings", { waitUntil: "commit" });
  await expect(page.getByText("Busy — retrying", { exact: true })).toBeVisible();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await page.getByRole("radio", { name: "Blue", exact: true }).check();
  await expect(page.locator("html")).toHaveAttribute("data-accent", "blue");
  await expect.poll(() => dispatches.length).toBe(2);
  expect(dispatches[1]! - dispatches[0]!).toBeGreaterThanOrEqual(1000);
  await expect(page.getByText("Busy — retrying", { exact: true })).toHaveCount(0);
  await clean();
});

test("renders core view ordering, counts and row semantics without reconstructing predicates", async ({
  page,
}) => {
  const clean = await observe(page, true);
  await seed(page);
  const basis = {
    observedAt: "2026-11-04T00:00:00.000000Z",
    businessDate: "2026-11-04",
    timeZone: "Etc/UTC",
  };
  await page.route("**/api/case.views", (route) =>
    route.fulfill({
      json: {
        outcome: {
          kind: "SUCCEEDED",
          data: {
            basis,
            items: [
              { view: "OVERDUE", count: 7 },
              { view: "NEEDS_ATTENTION", count: 2 },
            ],
          },
        },
      },
    }),
  );
  const requests: unknown[] = [];
  await page.route("**/api/case.list", (route) => {
    requests.push(route.request().postDataJSON());
    const row = caseSummaryInput({
      caseReference: "VISIBLE-MATCH",
      headline: "PAID",
      attention: ["DUE_TODAY"],
      responsible: actorRefInput({ displayName: { kind: "NAMED", value: "Responsible profile" } }),
      pendingReminders: 3,
      nextAction: {
        reminderId: "10000000-0000-4000-8000-000000000002",
        action: "Core next action",
        due: "2026-11-04",
        dueState: "TODAY",
      },
    });
    return route.fulfill({
      json: {
        outcome: {
          kind: "SUCCEEDED",
          data: {
            basis,
            items: [row],
            offers: [],
            nextCursor: null,
            exactReference: { caseId: row.caseId, caseReference: row.caseReference },
          },
        },
      },
    });
  });
  await page.route("**/api/case.summary", (route) =>
    route.fulfill({
      json: {
        outcome: {
          kind: "SUCCEEDED",
          data: {
            basis,
            counts: { total: 1, lifecycle: [], status: [], progress: [], assignment: [], due: [] },
            claimed: [],
            payable: [],
          },
        },
      },
    }),
  );
  await page.goto("/cases", { waitUntil: "commit" });
  await expect(page).toHaveURL(/\/cases\?view=OVERDUE$/u);
  const views = page.getByRole("tablist", { name: "Case view" }).getByRole("tab");
  await expect(views).toHaveText(["Overdue 7", "Needs attention 2"]);
  await expect(views.first()).toHaveAttribute("aria-selected", "true");
  await expect(page.locator(".case-headline")).toHaveText("Paid");
  await expect(page.locator(".worklist-next-action")).toContainText("Core next action");
  await expect(page.locator(".worklist-next-action")).toContainText("+2 more");
  await expect(page.locator(".worklist-responsible")).toContainText("Responsible profile");
  await expect(
    page.getByRole("region", { name: "Exact reference match", exact: true }),
  ).toBeVisible();
  expect(requests).toEqual([{ limit: 50, view: "OVERDUE" }]);
  await expectAccessible(page);
  await clean();
});

test("shell case creation returns to the captured core view and focus without sending a mutation", async ({
  page,
}) => {
  const clean = await observe(page, true);
  await seed(page);
  await page.route("**/api/context", (route) =>
    route.fulfill({
      json: {
        outcome: {
          kind: "SUCCEEDED",
          data: {
            requester: actorContextInput({
              productVersion: "synthetic-presentation",
              businessDate: "2026-11-04",
              timeZone: "Etc/UTC",
              capabilities: ["REGISTER"],
              views: ["CASES"],
              offers: [{ kind: "OPEN" }],
            }),
          },
        },
      },
    }),
  );
  const basis = {
    observedAt: "2026-11-04T00:00:00.000000Z",
    businessDate: "2026-11-04",
    timeZone: "Etc/UTC",
  };
  await page.route("**/api/case.list", (route) =>
    route.fulfill({
      json: {
        outcome: {
          kind: "SUCCEEDED",
          data: { basis, items: [], offers: [], nextCursor: null, exactReference: null },
        },
      },
    }),
  );
  await page.route("**/api/case.summary", (route) =>
    route.fulfill({
      json: {
        outcome: {
          kind: "SUCCEEDED",
          data: {
            basis,
            counts: { total: 0, lifecycle: [], status: [], progress: [], assignment: [], due: [] },
            claimed: [],
            payable: [],
          },
        },
      },
    }),
  );
  const writes: string[] = [];
  page.on("request", (request) => {
    if (/\/api\/operation\.(?:preview|execute)$/u.test(new URL(request.url()).pathname)) {
      writes.push(request.url());
    }
  });
  await page.goto("/cases?view=ALL&q=remember&order=RECENT", { waitUntil: "commit" });
  const origin = page.locator("#open-case");
  await expect(origin).toBeVisible();
  await expect(page.getByRole("button", { name: "Open a case", exact: true })).toHaveCount(0);
  await origin.click();
  await expect(page.getByRole("heading", { name: "Open a case", exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Back to cases", exact: true }).click();
  await expect(page).toHaveURL(/\/cases\?view=ALL&q=remember&order=RECENT$/u);
  await expect(origin).toBeFocused();
  expect(writes).toEqual([]);
  await clean();
});

test("reminder views keep URL state and leave implicit state predicates to the core", async ({
  page,
}) => {
  const clean = await observe(page, true);
  await seed(page);
  const requests: unknown[] = [];
  const forbidden: string[] = [];
  page.on("request", (request) => {
    if (new URL(request.url()).pathname === "/api/work.overview") {
      forbidden.push(request.url());
    }
  });
  await page.route("**/api/reminder.list", (route) => {
    requests.push(route.request().postDataJSON());
    return route.fulfill({
      json: {
        outcome: {
          kind: "SUCCEEDED",
          data: {
            basis: {
              observedAt: "2026-11-04T00:00:00.000000Z",
              businessDate: "2026-11-04",
              timeZone: "Etc/UTC",
            },
            items: [],
            nextCursor: null,
          },
        },
      },
    });
  });
  await page.goto("/reminders?view=COMPLETED&q=literal", { waitUntil: "commit" });
  const completed = page.getByRole("tab", { name: "Completed 0", exact: true });
  await expect(completed).toHaveAttribute("aria-selected", "true");
  await expect.poll(() => requests.length).toBe(1);
  const filter = page
    .getByRole("search", { name: "Reminders", exact: true })
    .getByRole("searchbox");
  await filter.fill("unsubmitted filter");
  await completed.focus();
  await page.keyboard.press("ArrowRight");
  await expect(page.getByRole("tab", { name: "Cancelled 0", exact: true })).toBeFocused();
  expect(requests).toHaveLength(1);
  await page.keyboard.press("Home");
  await page.keyboard.press("Enter");
  await expect(page).toHaveURL(/\/reminders\?view=PENDING&q=literal$/u);
  await expect.poll(() => requests.length).toBe(2);
  expect(requests).toEqual([
    { view: "COMPLETED", text: "literal", limit: 50 },
    { view: "PENDING", text: "literal", limit: 50 },
  ]);
  await expect(filter).toHaveValue("unsubmitted filter");
  expect(forbidden).toEqual([]);
  await expectAccessible(page);
  await clean();
});

const engine = process.env["CLAIMCORE_WEB_E2E_ENGINE"] ?? "chromium";
if (engine === "chromium") {
  for (const accent of ["green", "blue"] as const) {
    for (const mode of ["light", "dark"] as const) {
      test(`OS contrast modes preserve ${accent}-${mode} control boundaries and focus`, async ({
        page,
      }) => {
        const clean = await observe(page);
        await seed(page, { accent, mode });
        await page.emulateMedia({ contrast: "more" });
        await signInSettings(page);
        const picker = page.getByRole("combobox", { name: "Date and number format" });
        const more = await picker.evaluate((element) => {
          const style = getComputedStyle(element);
          const panel = element.closest(".settings-panel")!;
          return {
            border: style.borderTopColor,
            background: style.backgroundColor,
            preview: getComputedStyle(panel.querySelector(".settings-preview")!).color,
            primary: getComputedStyle(panel).color,
          };
        });
        expect(more.preview).toBe(more.primary);
        expect(
          contrastRatio(hexColor(more.border), hexColor(more.background)),
        ).toBeGreaterThanOrEqual(3);
        await page.emulateMedia({ contrast: "no-preference", forcedColors: "active" });
        await picker.focus();
        await page.keyboard.press("ArrowDown");
        await page.keyboard.press("Escape");
        await expect(picker).toBeFocused();
        await expect(picker).toHaveCSS("outline-width", "2px");
        const forced = await picker.evaluate((element) => {
          const style = getComputedStyle(element);
          return {
            border: style.borderTopColor,
            background: style.backgroundColor,
            borderStyle: style.borderTopStyle,
            focus: style.outlineColor,
            focusWidth: style.outlineWidth,
          };
        });
        expect(forced.borderStyle).toBe("solid");
        expect(forced.focusWidth).toBe("2px");
        expect(
          contrastRatio(hexColor(forced.border), hexColor(forced.background)),
        ).toBeGreaterThanOrEqual(3);
        expect(
          contrastRatio(hexColor(forced.focus, forced.background), hexColor(forced.background)),
        ).toBeGreaterThanOrEqual(3);
        await clean();
      });
    }
  }
}

for (const language of ["en", "ar"] as const) {
  test(`canonical timeline renders supplied money changes and names in ${language} under CSP`, async ({
    page,
    browserName,
  }, testInfo) => {
    const clean = await observe(page, true);
    await seed(page, {
      language,
      accent: language === "ar" ? "blue" : "green",
      mode: language === "ar" ? "dark" : "light",
    });
    if (language === "ar") {
      await page.setViewportSize({ width: 390, height: 844 });
    }
    const basis = {
      observedAt: "2026-11-04T12:00:00.000000Z",
      businessDate: "2026-11-04",
      timeZone: "Etc/UTC",
    };
    const target = { kind: "ID" as const, caseId: "20000000-0000-4000-8000-000000000001" };
    const current = caseViewInput({
      caseId: target.caseId,
      caseReference: "CASE-1",
      revision: 1,
      advice: { acts: [], history: ["SUMMARY", "FULL"] },
      state: { kind: "ACTIVE", fields: caseFields },
    });
    await page.route("**/api/case.get", (route) =>
      route.fulfill({
        json: {
          outcome: { kind: "SUCCEEDED", data: { ...current, work: { ...current.work, basis } } },
        },
      }),
    );
    let reads = 0;
    await page.route("**/api/case.timeline", (route) => {
      reads += 1;
      const input = route.request().postDataJSON() as { filter?: string };
      const entry = timelineEntry({
        recordedBy: actorRefInput({ displayName: { kind: "NAMED", value: "Timeline recorder" } }),
        changes: {
          kind: "DISCLOSED",
          items: [
            {
              kind: "CASE_MONEY",
              change: {
                field: "payableAmount",
                change: {
                  before: { amount: "150000", currency: "JPY" },
                  after: { amount: "1234.5678", currency: "EUR" },
                },
              },
            },
          ],
        },
      });
      return route.fulfill({
        json: {
          outcome: {
            kind: "SUCCEEDED",
            data: {
              ...timelinePage([
                input.filter === "WORK"
                  ? {
                      ...entry,
                      kind: "WORK",
                      act: "UPDATE_WORK",
                      changes: {
                        kind: "DISCLOSED",
                        items: [
                          {
                            kind: "HANDLING_SUMMARY",
                            change: { before: null, after: "Core work change" },
                          },
                        ],
                      },
                    }
                  : entry,
              ]),
              basis,
            },
          },
        },
      });
    });
    const retired: string[] = [];
    page.on("request", (request) => {
      if (/\/api\/case\.(?:history|activity)$/u.test(request.url())) {
        retired.push(request.url());
      }
    });
    await page.goto(casePath(target.caseId), { waitUntil: "commit" });
    await expect(page.getByText("Timeline recorder", { exact: true })).toBeVisible();
    await expect(page.locator(".case-progress .case-stage-step")).toHaveCount(4);
    await expect(page.locator(".case-stage-step").first().locator("time")).toHaveAttribute(
      "datetime",
      "2026-09-01",
    );
    const timeline = page.locator(".case-timeline-panel");
    await expect(timeline.locator("del")).toContainText("JPY");
    await expect(timeline.locator("ins")).toContainText("EUR");
    expect(reads).toBe(1);
    expect(retired).toEqual([]);
    await expectAccessible(page);
    await clean();
    if (browserName === "chromium") {
      const camera = await page.context().newCDPSession(page);
      const capture = await camera.send("Page.captureScreenshot", {
        format: "png",
        captureBeyondViewport: true,
      });
      const path = testInfo.outputPath(`case-timeline-${language}.png`);
      await writeFile(path, Buffer.from(capture.data, "base64"));
      await testInfo.attach("case-timeline", { path, contentType: "image/png" });
      await camera.detach();
    }
    await timeline.locator("summary").click();
    await expect(timeline.locator("details")).toContainText(entryOperationId);
    const filters = timeline.locator(".timeline-filters button");
    await filters.nth(2).click();
    await expect(timeline).toContainText("Core work change");
    expect(reads).toBe(2);
    await expectAccessible(page);
    await clean();
  });
}

for (const kind of ["NOT_REGISTERED", "NO_ROLES", "DISABLED"] as const) {
  test(`caller-owned ${kind} access state does not launch resource reads under CSP`, async ({
    page,
  }) => {
    const clean = await observe(page, true);
    await seed(page);
    let requester: RequesterState;
    if (kind === "NOT_REGISTERED") {
      requester = {
        kind,
        issuer: "https://issuer.example/",
        subject: "own-account",
        claims: { name: "Unused IdP hint" },
      };
    } else if (kind === "DISABLED") {
      requester = { kind, self: actorRefInput({ availability: "DISABLED" }) };
    } else {
      requester = {
        kind,
        self: actorRefInput(),
        installationId: "90000000-0000-4000-8000-000000000001",
        businessDate: "2026-11-04",
        timeZone: "Etc/UTC",
      };
    }
    await page.route("**/api/context", (route) =>
      route.fulfill({ json: { outcome: { kind: "SUCCEEDED", data: { requester } } } }),
    );
    const resources: string[] = [];
    page.on("request", (request) => {
      if (/\/api\/(?:case|reminder|authority)\./u.test(request.url())) {
        resources.push(request.url());
      }
    });
    await page.goto("/cases", { waitUntil: "commit" });
    await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
    await expect(page.getByRole("button", { name: "Sign out", exact: true })).toBeVisible();
    expect(resources).toEqual([]);
    await expect(page.getByText("Unused IdP hint")).toHaveCount(0);
    await expectAccessible(page);
    await clean();
  });
}

test("same-requester access loss retains unknown bytes and a visible departure guard under CSP", async ({
  page,
}) => {
  const clean = await observe(page, true);
  await seed(page);
  const active = actorContextInput({
    productVersion: "synthetic-presentation",
    businessDate: "2026-11-04",
    timeZone: "Etc/UTC",
    capabilities: ["MANAGE_AUTHORITY", "READ_AUTHORITY"],
    views: ["AUTHORITY", "OPERATIONS"],
    offers: [{ kind: "AUTHORITY_ACT", act: "REGISTER" }],
  });
  let hasAccess = true;
  await page.route("**/api/context", (route) =>
    route.fulfill({
      json: {
        outcome: {
          kind: "SUCCEEDED",
          data: {
            requester: hasAccess
              ? active
              : {
                  kind: "NO_ROLES",
                  self: active.self,
                  installationId: active.installationId,
                  businessDate: active.businessDate,
                  timeZone: active.timeZone,
                },
          },
        },
      },
    }),
  );
  await page.route("**/api/authority.list", (route) =>
    route.fulfill({
      json: { outcome: { kind: "SUCCEEDED", data: { items: [], nextCursor: null } } },
    }),
  );
  await page.route("**/api/operation.preview", (route) => {
    const input: unknown = route.request().postDataJSON();
    if (
      !isRequest("operation.preview", input) ||
      input.kind !== "AUTHORITY" ||
      input.act.kind !== "REGISTER"
    ) {
      throw new Error("Expected the browser's real registration preview request.");
    }
    return route.fulfill({
      json: {
        operationId: input.operationId,
        outcome: {
          kind: "REVIEWED",
          data: {
            review: {
              subject: {
                kind: "AUTHORITY",
                target: null,
                accessScope: { kind: "INSTALLATION" },
                before: { enabled: false, profile: null, grants: [], access: [] },
                after: {
                  enabled: true,
                  profile: { displayName: input.act.profile.displayName, contact: null },
                  grants: [],
                  access: [],
                },
              },
            },
          },
        },
      },
    });
  });
  const bytes: string[] = [];
  await page.route("**/api/operation.execute", (route) => {
    const input: unknown = route.request().postDataJSON();
    if (!isRequest("operation.execute", input)) {
      throw new Error("Expected a typed exact retry request.");
    }
    const body = route.request().postData();
    if (body === null) {
      throw new Error("Expected exact request bytes.");
    }
    bytes.push(body);
    return route.fulfill({
      json: { operationId: input.operationId, outcome: { kind: "OUTCOME_UNKNOWN", data: null } },
    });
  });
  await page.goto("/people", { waitUntil: "commit" });
  await page.locator('[name="target.issuer"]').fill("https://issuer.example/");
  await page.locator('[name="target.subject"]').fill("retained-person");
  await page.locator('[name="act.profile.displayName"]').fill("Retained person");
  await page.getByRole("button", { name: "Review changes", exact: true }).click();
  await page.getByRole("checkbox").check();
  await page.getByRole("button", { name: "Record changes", exact: true }).click();
  await expect(page.getByRole("button", { name: "Send again", exact: true })).toBeVisible();
  expect(bytes).toHaveLength(1);
  hasAccess = false;
  await page.evaluate(() => {
    window.dispatchEvent(new Event("focus"));
  });
  await expect(
    page.getByRole("heading", { name: "You don’t have access yet", exact: true }),
  ).toBeVisible();
  expect(bytes).toHaveLength(1);
  await expect(page.getByRole("button", { name: "Send again", exact: true })).toHaveCount(0);
  await page.getByRole("button", { name: "Sign out", exact: true }).click();
  await expect(page.getByRole("dialog")).toBeVisible();
  await expectAccessible(page);
  await page.getByRole("button", { name: "Keep editing", exact: true }).click();
  await expect(page.getByRole("button", { name: "Sign out", exact: true })).toBeFocused();
  hasAccess = true;
  await page.getByRole("button", { name: "Check access", exact: true }).click();
  await page.getByRole("button", { name: "Send again", exact: true }).click();
  await expect.poll(() => bytes.length).toBe(2);
  expect(bytes[1]).toBe(bytes[0]);
  await expectAccessible(page);
  await clean();
});
