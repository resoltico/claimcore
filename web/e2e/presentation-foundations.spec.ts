import { expect, test, type Page } from "@playwright/test";
import { consumerContractIdentity } from "@contracts/client";
import { actorContextInput } from "../tests/support/inputs";
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
    await page.route("**/api/context", (route) =>
      route.fulfill({
        json: {
          outcome: {
            kind: "SUCCEEDED",
            data: actorContextInput({
              productVersion: "synthetic-presentation",
              businessDate: "2026-11-04",
              timeZone: "Etc/UTC",
              capabilities: ["READ_CASE", "READ_AUTHORITY"],
              views: ["CASES", "AUTHORITY", "OPERATIONS"],
              offers: [],
            }),
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
          data: actorContextInput({
            productVersion: "synthetic-presentation",
            businessDate: "2026-11-04",
            timeZone: "Etc/UTC",
            capabilities: [],
            views: [],
            offers: [],
          }),
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
