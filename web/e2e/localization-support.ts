import { expect, type Page, type Locator, type APIResponse } from "@playwright/test";
import { chooseOption, settledWorkspaceHeader } from "./workspace-navigation";
import type { EndpointId } from "@contracts/client";
import { endpoints } from "@contracts/client";
import type { ServiceResponse } from "../src/protocol/types";
import { strictJsonBytes } from "../src/api/responseBytes";
import { isHostFailure, isResponse } from "../src/protocol/client";
import { preferenceKey } from "../src/presentation/preferences";
import type { Language, DisplayLocale, Preferences } from "../src/presentation/preferences";
import enCore from "../../src/ClaimCore.Protocol/Messages/Localization/en.json" with { type: "json" };
import lvCore from "../../src/ClaimCore.Protocol/Messages/Localization/lv.json" with { type: "json" };
import arCore from "../../src/ClaimCore.Protocol/Messages/Localization/ar.json" with { type: "json" };

import enChrome from "../locales/en.json" with { type: "json" };
const en = { ...enCore, ...enChrome };
import lvChrome from "../locales/lv.json" with { type: "json" };
const lv = { ...lvCore, ...lvChrome };
import arChrome from "../locales/ar.json" with { type: "json" };
const ar = { ...arCore, ...arChrome };

import type { ReviewIdentity } from "./case-workflow";

export const seedPresentation = async (
  page: Page,
  language: Language,
  displayLocale: DisplayLocale,
): Promise<void> => {
  await page.addInitScript(
    ({ key, preferences }) => {
      localStorage.setItem(key, JSON.stringify(preferences));
    },
    {
      key: preferenceKey,
      preferences: {
        version: 3,
        language,
        displayLocale,
        mode: "system",
        accent: "green" as const,
      },
    },
  );
};
export const ui = (language: "en" | "lv" | "ar", key: keyof typeof en): string =>
  ({ en, lv, ar })[language][key];
export const openLabel = (language: "en" | "lv" | "ar"): string =>
  ({ en, lv, ar })[language]["act.OPEN.label"];
type PreferenceVisit =
  { kind: "CURRENT" } | { kind: "DIALOG" } | { kind: "PAGE"; returnUrl: string };
/** Expose the real Settings navigation without accepting a draft/uncertainty guard. */
export const settingsNavigation = async (page: Page): Promise<Locator> => {
  const drawerSettings = page.locator('[role="dialog"] [data-open-settings]');
  if (await drawerSettings.isVisible()) {
    return drawerSettings;
  }
  await settledWorkspaceHeader(page);
  const compact = await page.evaluate(() => window.matchMedia("(width < 40rem)").matches);
  if (compact) {
    return page.locator('.workspace-mobile-tabs a[href="/settings"]');
  }
  const account = page.locator(".workspace-sidebar .account-menu");
  if ((await account.getAttribute("open")) === null) {
    await account.locator(":scope > summary").click();
  }
  const link = account.locator('a[href="/settings"]');
  return link;
};
const openPreferences = async (page: Page): Promise<PreferenceVisit> => {
  const panel = page.locator(".settings-panel");
  if (await panel.isVisible()) {
    return { kind: "CURRENT" };
  }
  const footer = page.locator(".login-footer button");
  if (await footer.isVisible()) {
    await footer.click();
    await expect(panel).toBeVisible();
    return { kind: "DIALOG" };
  }
  const returnUrl = page.url();
  await (await settingsNavigation(page)).click();
  await expect(page).toHaveURL((url) => url.pathname === "/settings");
  await expect(panel).toBeVisible();
  return { kind: "PAGE", returnUrl };
};
const closePreferences = async (page: Page, visit: PreferenceVisit): Promise<void> => {
  if (visit.kind === "DIALOG") {
    await page.keyboard.press("Escape");
  }
  if (visit.kind === "PAGE") {
    const back = page.locator("[data-return-workspace]");
    if (await back.isVisible()) {
      await back.click();
    } else {
      await page.goBack();
    }
    await expect(page).toHaveURL(visit.returnUrl);
  }
};
/** Request through Settings; callers may hold a language asset before awaiting activation. */
export const requestLanguage = async (page: Page, language: Language): Promise<void> => {
  const visit = await openPreferences(page);
  const control = page.locator(
    `.settings-panel input[type="radio"][name$="-language"][value="${language}"]`,
  );
  await control.check();
  await expect(control).toBeChecked();
  await closePreferences(page, visit);
};
export const selectLanguage = async (page: Page, language: Language): Promise<void> => {
  await requestLanguage(page, language);
  await expect(page.locator("html")).toHaveAttribute(
    "lang",
    language === "en-XA" ? "en" : language,
  );
  await expect(page.locator("html")).toHaveAttribute("dir", language === "ar" ? "rtl" : "ltr");
};
export const selectFormat = async (page: Page, locale: DisplayLocale): Promise<void> => {
  const visit = await openPreferences(page);
  const control = page.locator('.settings-panel [role="combobox"][id$="-format"]');
  await chooseOption(page, control, locale);
  await expect(control).toHaveAttribute("value", locale);
  await closePreferences(page, visit);
};
export const selectMode = async (page: Page, mode: Preferences["mode"]): Promise<void> => {
  const visit = await openPreferences(page);
  const control = page.locator(
    `.settings-panel input[type="radio"][name$="-mode"][value="${mode}"]`,
  );
  await control.check();
  await expect(control).toBeChecked();
  await closePreferences(page, visit);
};
export const expectPreferences = async (
  page: Page,
  expected: { language?: Language; displayLocale?: DisplayLocale },
) => {
  const visit = await openPreferences(page);
  if (expected.language !== undefined) {
    await expect(
      page.locator(
        `.settings-panel input[type="radio"][name$="-language"][value="${expected.language}"]`,
      ),
    ).toBeChecked();
  }
  if (expected.displayLocale !== undefined) {
    await expect(page.locator('.settings-panel [role="combobox"][id$="-format"]')).toHaveAttribute(
      "value",
      expected.displayLocale,
    );
  }
  await closePreferences(page, visit);
};
// Session checks and autonomous work-date observations run independently of foreground editing.
// Keep every other endpoint, including all mutations, in exact request-count assertions.
export const trackForegroundRequests = (page: Page) => {
  const requests: string[] = [];
  page.on("request", (request) => {
    const path = new URL(request.url()).pathname;
    if (
      endpoints.some(
        (endpoint) =>
          endpoint.id !== "session" && endpoint.id !== "work.overview" && endpoint.path === path,
      )
    ) {
      requests.push(`${request.method()} ${path}`);
    }
  });
  return requests;
};
export const reviewFrom = (
  value: ServiceResponse<"operation.preview">,
  bytes: Buffer,
): ReviewIdentity => {
  if (value.outcome.kind !== "REVIEWED") {
    throw new Error("E2E_LOCALIZATION_PREPARATION_REFUSED");
  }
  const draft: unknown = JSON.parse(bytes.toString("utf8"));
  if (
    typeof draft !== "object" ||
    draft === null ||
    !("operationId" in draft) ||
    typeof draft.operationId !== "string"
  ) {
    throw new Error("Missing operation identity.");
  }
  const { operationId } = draft;
  return { operationId };
};
const deferred = <T>() => {
  let resolve: (value: T) => void = () => undefined;
  let reject: (reason: Error) => void = () => undefined;
  const promise = new Promise<T>((yes, no) => {
    resolve = yes;
    reject = no;
  });
  return { promise, resolve, reject };
};
type Captured<T> = { reply: T; bytes: Buffer };
const replyCategory = (endpoint: EndpointId, status: number, payload: unknown): string => {
  if (isHostFailure(payload, status)) {
    return `HOST_${payload.hostFailure.code}_${payload.hostFailure.phase ?? "UNSPECIFIED"}`;
  }
  return isResponse(endpoint, payload) ? payload.outcome.kind : "UNRECOGNIZED";
};
const readReply = async (response: APIResponse): Promise<unknown> => {
  if (
    response.headers()["content-type"]?.split(";", 1)[0]?.trim().toLowerCase() !==
    "application/json"
  ) {
    throw new Error("Unsupported response media type.");
  }
  return strictJsonBytes(await response.body());
};
const pauseReply = async <T>(
  page: Page,
  endpoint: EndpointId,
  admit: (value: unknown, status: number) => T,
  drop: boolean,
) => {
  const path = endpoints.find((entry) => entry.id === endpoint)?.path;
  if (path === undefined) {
    throw new Error("E2E_LOCALIZATION_ENDPOINT_MISSING");
  }
  const ready = deferred<Captured<T>>();
  const released = deferred<void>();
  await page.route(
    `**${path}`,
    async (route) => {
      let category = "TRANSPORT";
      try {
        const response = await route.fetch({ maxRedirects: 0 });
        const status = response.status();
        category = `STATUS_${status}_INVALID_JSON`;
        const payload = await readReply(response);
        category = `STATUS_${status}_${replyCategory(endpoint, status, payload)}`;
        const reply = admit(payload, status);
        const bytes = route.request().postDataBuffer();
        if (bytes === null) {
          throw new Error("Missing request bytes.");
        }
        ready.resolve({ reply, bytes });
        await released.promise;
        if (drop) {
          await route.abort("connectionfailed");
        } else {
          await route.fulfill({ response });
        }
      } catch {
        ready.reject(new Error(`E2E_LOCALIZATION_PAUSED_REQUEST_FAILED_${category}`));
      }
    },
    { times: 1 },
  );
  return {
    ready: ready.promise,
    release: () => {
      released.resolve();
    },
  };
};
export const pauseJsonReply = <K extends EndpointId>(page: Page, endpoint: K, drop = false) =>
  pauseReply(
    page,
    endpoint,
    (payload, status): ServiceResponse<K> => {
      if (status !== 200 || !isResponse(endpoint, payload)) {
        throw new Error("Expected a typed endpoint reply.");
      }
      return payload;
    },
    drop,
  );
export { noHorizontalOverflow } from "./layout-support";

export const expectKeyboardContained = async (page: Page, dialog: Locator): Promise<void> => {
  const challenge = dialog.locator(".irreversible-confirmation input");
  const start =
    (await challenge.count()) === 1 ? challenge : dialog.locator("button[data-confirm-operation]");
  await start.focus();
  for (let index = 0; index < 12; index += 1) {
    await page.keyboard.press("Tab");
    expect(await dialog.evaluate((element) => element.contains(document.activeElement))).toBe(true);
  }
};

/** Assert the actual consent surface without submitting; callers explicitly press the act button. */
export const confirmReview = async (page: Page): Promise<void> => {
  const dialog = page.getByRole("dialog");
  const action = dialog.locator("button[data-confirm-operation]");
  await expect(action).toHaveCount(1);
  await expect(action).toBeVisible();
  const challenge = dialog.locator(".irreversible-confirmation input");
  if ((await challenge.count()) === 1) {
    // A destructive caller must author the known exact target itself; a generic helper cannot consent to it.
    await expect(challenge).toBeVisible();
    await expect(action).toBeDisabled();
    await challenge.focus();
    return;
  }
  await expect(dialog.getByRole("checkbox")).toHaveCount(0);
  await expect(action).toBeEnabled();
  await action.focus();
};

export const businessFormatRefusalText = {
  CALENDAR_DATE_REQUIRED: {
    en: "Use one valid calendar date in YYYY-MM-DD format.",
    lv: "Izmantojiet vienu derīgu datumu formātā YYYY-MM-DD.",
    ar: "استخدم تاريخًا تقويميًا صالحًا واحدًا بالصيغة YYYY-MM-DD.",
  },
  DECIMAL_FORMAT: {
    en: "Use non-negative decimal text: up to 18 integer digits and 4 fractional digits; no sign, exponent or redundant leading zeros: 0.10 is valid; 00.10 is invalid.",
    lv: "Izmantojiet nenegatīvu decimālskaitli: līdz 18 veseliem cipariem un 4 daļas cipariem; bez zīmes vai eksponenta.",
    ar: "استخدم نصًا عشريًا غير سالب: حتى \u206818\u2069 رقمًا صحيحًا و\u20684\u2069 أرقام كسرية؛ دون إشارة أو أس.",
  },
  CURRENCY_FORMAT: {
    en: "Use three uppercase ASCII letters (A–Z) for the currency identifier.",
    lv: "Izmantojiet trīs lielos ASCII burtus (A–Z) valūtas apzīmējumam.",
    ar: "استخدم ثلاثة أحرف ASCII كبيرة من A إلى Z لمعرّف العملة.",
  },
} as const;

export const expectBusinessFormatRefusal = async (
  page: Page,
  field: string,
  diagnostic: keyof typeof businessFormatRefusalText,
  submit: () => Promise<void>,
) => {
  const pending = await pauseJsonReply(page, "operation.preview");
  try {
    await submit();
    const { outcome } = (await pending.ready).reply;
    expect(outcome.kind).toBe("REJECTED");
    if (outcome.kind !== "REJECTED") {
      throw new Error("Expected a typed business format refusal.");
    }
    expect(outcome.data).toEqual({
      code: "INVALID_INPUT",
      action: "CORRECT_INPUT",
      issues: [
        {
          field,
          code: diagnostic,
          ...(diagnostic === "DECIMAL_FORMAT"
            ? { maximumIntegerDigits: 18, maximumFractionalDigits: 4 }
            : {}),
        },
      ],
    });
  } finally {
    pending.release();
  }
};
