import { expect, type Page, type Locator, type APIResponse } from "@playwright/test";
import type { EndpointId } from "@contracts/client";
import { endpoints } from "@contracts/client";
import type { ServiceResponse } from "../src/api/types";
import { strictJsonBytes } from "../src/api/responseBytes";
import { isHostFailure, isResponse } from "../src/protocol/client";
import { preferenceKey } from "../src/presentation/preferences";
import type { Language, DisplayLocale } from "../src/presentation/preferences";
import en from "../../src/ClaimCore.Contract/Localization/en.json" with { type: "json" };
import lv from "../../src/ClaimCore.Contract/Localization/lv.json" with { type: "json" };
import ar from "../../src/ClaimCore.Contract/Localization/ar.json" with { type: "json" };

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
    { key: preferenceKey, preferences: { version: 1, language, displayLocale } },
  );
};
export const ui = (language: "en" | "lv" | "ar", key: keyof typeof en): string =>
  ({ en, lv, ar })[language][key];
export const openLabel = (language: "en" | "lv" | "ar"): string =>
  ({ en, lv, ar })[language]["act.OPEN.label"];
// Role lookup includes inert background controls; require the active native control.
export const selectLanguage = async (page: Page, language: Language): Promise<void> => {
  const control = page
    .getByRole("combobox")
    .and(page.locator("select:not([inert], [inert] *)"))
    .and(page.locator('select[id$="-language"]'));
  await expect(control).toHaveCount(1);
  await control.selectOption(language);
  await expect(control).toHaveValue(language);
  await expect(page.locator("html")).toHaveAttribute(
    "lang",
    language === "en-XA" ? "en" : language,
  );
  await expect(page.locator("html")).toHaveAttribute("dir", language === "ar" ? "rtl" : "ltr");
};
export const selectFormat = async (page: Page, locale: DisplayLocale): Promise<void> => {
  const control = page
    .getByRole("combobox")
    .and(page.locator("select:not([inert], [inert] *)"))
    .and(page.locator('select[id$="-format"]'));
  await expect(control).toHaveCount(1);
  await control.selectOption(locale);
  await expect(control).toHaveValue(locale);
};
export const trackRequests = (page: Page) => {
  const requests: string[] = [];
  page.on("request", (request) => {
    const path = new URL(request.url()).pathname;
    if (endpoints.some((endpoint) => endpoint.id !== "session" && endpoint.path === path)) {
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
  await dialog.getByRole("checkbox").focus();
  for (let index = 0; index < 12; index += 1) {
    await page.keyboard.press("Tab");
    expect(await dialog.evaluate((element) => element.contains(document.activeElement))).toBe(true);
  }
};

export const confirmReview = async (page: Page): Promise<void> => {
  const dialog = page.getByRole("dialog");
  const checkbox = dialog.getByRole("checkbox");
  await dialog
    .locator("label")
    .filter({ has: page.getByRole("checkbox") })
    .click();
  await expect(checkbox).toBeChecked();
};

export const businessFormatRefusalText = {
  INPUT_CALENDAR_DATE_REQUIRED: {
    en: "Use one valid calendar date in YYYY-MM-DD format.",
    lv: "Izmantojiet vienu derīgu datumu formātā YYYY-MM-DD.",
    ar: "استخدم تاريخًا تقويميًا صالحًا واحدًا بالصيغة YYYY-MM-DD.",
  },
  INPUT_DECIMAL_FORMAT: {
    en: "Use non-negative decimal text: up to 18 integer digits and 4 fractional digits; no sign, exponent or redundant leading zeros: 0.10 is valid; 00.10 is invalid.",
    lv: "Izmantojiet nenegatīvu decimālskaitli: līdz 18 veseliem cipariem un 4 daļas cipariem; bez zīmes vai eksponenta.",
    ar: "استخدم نصًا عشريًا غير سالب: حتى \u206818\u2069 رقمًا صحيحًا و\u20684\u2069 أرقام كسرية؛ دون إشارة أو أس.",
  },
  INPUT_CURRENCY_FORMAT: {
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
    expect(outcome.data.target).toBe(field);
    expect(outcome.data.code).toBe(diagnostic);
    expect(outcome.data.action).toBe("CORRECT_INPUT");
    if (diagnostic === "INPUT_DECIMAL_FORMAT") {
      expect(outcome.data.limits).toEqual({
        maximumIntegerDigits: 18,
        maximumFractionalDigits: 4,
      });
    }
  } finally {
    pending.release();
  }
};
