import { writeFile } from "node:fs/promises";

import { expect, type Page, type Locator } from "@playwright/test";
import type { WebV3EndpointId } from "../src/generated/contracts/web-v3.endpoint-catalog";
import { webV3Endpoints } from "../src/generated/contracts/web-v3.endpoint-catalog";
import type { WebV3Response } from "../src/generated/contracts/web-v3.types";
import { isWebV3Response } from "../src/generated/contracts/web-v3.validation";
import { preferenceKey } from "../src/presentation/preferences";
import type { Language, DisplayLocale } from "../src/presentation/preferences";
import en from "../src/presentation/catalogs/en.ui.json" with { type: "json" };
import lv from "../src/presentation/catalogs/lv.ui.json" with { type: "json" };
import ar from "../src/presentation/catalogs/ar.ui.json" with { type: "json" };
import enPresentation from "../src/presentation/generated/en.json" with { type: "json" };
import lvPresentation from "../src/presentation/generated/lv.json" with { type: "json" };
import arPresentation from "../src/presentation/generated/ar.json" with { type: "json" };
import type { PreparedIdentity } from "./case-workflow";

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
  ({ en: enPresentation, lv: lvPresentation, ar: arPresentation })[language]["command.OPEN.label"]
    .map((part) => part.value)
    .join("");
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
    if (path.startsWith("/api/v3/")) {
      requests.push(`${request.method()} ${path}`);
    }
  });
  return requests;
};
export const preparedFrom = (value: WebV3Response<"command.prepare">): PreparedIdentity => {
  if (value.outcome.tag !== "PREPARED") {
    throw new Error("E2E_LOCALIZATION_PREPARATION_REFUSED");
  }
  const { operationId, requestSha256 } = value.outcome.data.details.summary;
  if (requestSha256 === null) {
    throw new Error("E2E_LOCALIZATION_EXACT_DIGEST_MISSING");
  }
  return { operationId, requestSha256 };
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
type Captured<K extends WebV3EndpointId> = {
  reply: WebV3Response<K>;
  bytes: Buffer;
  sourceDigest: string | undefined;
};
export const pauseJsonReply = async <K extends WebV3EndpointId>(
  page: Page,
  endpoint: K,
  drop = false,
) => {
  const path = webV3Endpoints.find((entry) => entry.id === endpoint)?.path;
  if (path === undefined) {
    throw new Error("E2E_LOCALIZATION_ENDPOINT_MISSING");
  }
  const ready = deferred<Captured<K>>();
  const released = deferred<void>();
  await page.route(
    `**${path}`,
    async (route) => {
      try {
        const response = await route.fetch();
        const payload: unknown = await response.json();
        if (response.status() !== 200 || !(await isWebV3Response(endpoint, payload))) {
          throw new Error("E2E_LOCALIZATION_NATIVE_REPLY_INVALID");
        }
        const bytes = route.request().postDataBuffer();
        if (bytes === null) {
          throw new Error("E2E_LOCALIZATION_REQUEST_BYTES_MISSING");
        }
        ready.resolve({
          reply: payload as WebV3Response<K>,
          bytes,
          sourceDigest: route.request().headers()["x-claimcore-source-sha256"],
        });
        await released.promise;
        if (drop) {
          await route.abort("connectionfailed");
        } else {
          await route.fulfill({ response });
        }
      } catch {
        ready.reject(new Error("E2E_LOCALIZATION_PAUSED_REQUEST_FAILED"));
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
export const inspectPending = async (page: Page, identity: PreparedIdentity): Promise<void> => {
  await page.getByRole("button", { name: "Recovery", exact: true }).click();
  const row = page.locator(".recovery-list li").filter({ hasText: identity.operationId });
  await expect(row).toHaveCount(1);
  await row.getByRole("button", { name: "Inspect", exact: true }).click();
  await expect(page.getByRole("dialog")).toContainText(identity.requestSha256);
};
const collectLayoutMetrics = () => {
  const { clientWidth } = document.documentElement;
  const offenders = [...document.querySelectorAll("*")]
    .map((element) => ({ element, rect: element.getBoundingClientRect() }))
    .filter(({ rect }) => rect.left < -1 || rect.right > clientWidth + 1)
    .slice(0, 5)
    .map(({ element, rect }) => ({
      tag: element.tagName.toLowerCase(),
      className: typeof element.className === "string" ? element.className : "",
      left: Math.round(rect.left),
      right: Math.round(rect.right),
      width: Math.round(rect.width),
    }));
  const scrollable = [...document.querySelectorAll("*")]
    .filter(
      (element) =>
        element !== document.documentElement &&
        element !== document.body &&
        element.scrollWidth > element.clientWidth + 1,
    )
    .sort((left, right) => right.scrollWidth - left.scrollWidth)
    .slice(0, 10)
    .map((element) => ({
      tag: element.tagName.toLowerCase(),
      className: typeof element.className === "string" ? element.className : "",
      clientWidth: element.clientWidth,
      scrollWidth: element.scrollWidth,
    }));
  return {
    clientWidth,
    scrollWidth: document.documentElement.scrollWidth,
    offenders,
    scrollable,
  };
};
export const noHorizontalOverflow = async (page: Page, stage: "narrow" | "zoom"): Promise<void> => {
  const metrics = await page.evaluate(collectLayoutMetrics);
  if (metrics.scrollWidth <= metrics.clientWidth + 1) {
    return;
  }
  const engine = process.env["CLAIMCORE_WEB_E2E_ENGINE"];
  if (engine !== undefined && ["chromium", "firefox", "webkit"].includes(engine)) {
    const report = new URL(
      `../../artifacts/browser/localization-layout-${engine}.json`,
      import.meta.url,
    );
    const offenders = metrics.offenders.map(({ tag, className, left, right, width }) => ({
      tag,
      className: className.replace(/[^a-zA-Z0-9_-]/gu, "").slice(0, 40),
      left,
      right,
      width,
    }));
    const scrollable = metrics.scrollable.map(({ tag, className, clientWidth, scrollWidth }) => ({
      tag,
      className: className.replace(/[^a-zA-Z0-9_-]/gu, "").slice(0, 40),
      clientWidth,
      scrollWidth,
    }));
    await writeFile(report, `${JSON.stringify({ stage, ...metrics, offenders, scrollable })}\n`);
  }
  throw new Error(`E2E_LOCALIZATION_${stage.toUpperCase()}_OVERFLOW`);
};

export const expectKeyboardContained = async (page: Page, dialog: Locator): Promise<void> => {
  await dialog.getByRole("checkbox").focus();
  for (let index = 0; index < 12; index += 1) {
    await page.keyboard.press("Tab");
    expect(await dialog.evaluate((element) => element.contains(document.activeElement))).toBe(true);
  }
};

export const confirmPrepared = async (page: Page): Promise<void> => {
  const dialog = page.getByRole("dialog");
  const checkbox = dialog.getByRole("checkbox");
  await dialog
    .locator("label")
    .filter({ has: page.getByRole("checkbox") })
    .click();
  await expect(checkbox).toBeChecked();
};
