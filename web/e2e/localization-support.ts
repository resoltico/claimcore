import { expect, type Page, type Locator } from "@playwright/test";
import type { WebV2EndpointId } from "../src/generated/convergence/web-v2.endpoint-catalog";
import { webV2Endpoints } from "../src/generated/convergence/web-v2.endpoint-catalog";
import type { WebV2Response } from "../src/generated/convergence/web-v2.types";
import { isWebV2Response } from "../src/generated/convergence/web-v2.validation";
import type { Language, DisplayLocale } from "../src/presentation/preferences";
import en from "../src/presentation/catalogs/en.ui.json" with { type: "json" };
import lv from "../src/presentation/catalogs/lv.ui.json" with { type: "json" };
import ar from "../src/presentation/catalogs/ar.ui.json" with { type: "json" };
import type { PreparedIdentity } from "./case-workflow";

export const ui = (language: "en" | "lv" | "ar", key: keyof typeof en): string =>
  ({ en, lv, ar })[language][key];
export const selectLanguage = async (page: Page, language: Language): Promise<void> => {
  const dialog = page.getByRole("dialog");
  const scope = (await dialog.count()) === 1 ? dialog : page;
  const control = scope
    .getByRole("combobox")
    .filter({ has: page.locator('option[value="en-XA"]') });
  await control.selectOption(language);
  await expect(control).toHaveValue(language);
  await expect(page.locator("html")).toHaveAttribute(
    "lang",
    language === "en-XA" ? "en" : language,
  );
  await expect(page.locator("html")).toHaveAttribute("dir", language === "ar" ? "rtl" : "ltr");
};
export const selectFormat = async (page: Page, locale: DisplayLocale): Promise<void> => {
  const dialog = page.getByRole("dialog");
  const scope = (await dialog.count()) === 1 ? dialog : page;
  const control = scope
    .getByRole("combobox")
    .filter({ has: page.locator('option[value="en-GB"]') });
  await control.selectOption(locale);
  await expect(control).toHaveValue(locale);
};
export const trackRequests = (page: Page) => {
  const requests: string[] = [];
  page.on("request", (request) => {
    const path = new URL(request.url()).pathname;
    if (path.startsWith("/api/v2/")) requests.push(`${request.method()} ${path}`);
  });
  return requests;
};
export const preparedFrom = (value: WebV2Response<"command.prepare">): PreparedIdentity => {
  if (value.outcome.tag !== "PREPARED") throw new Error("E2E_LOCALIZATION_PREPARATION_REFUSED");
  const { operationId, requestSha256 } = value.outcome.data.details.summary;
  if (requestSha256 === null) throw new Error("E2E_LOCALIZATION_EXACT_DIGEST_MISSING");
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
type Captured<K extends WebV2EndpointId> = {
  reply: WebV2Response<K>;
  bytes: Buffer;
  sourceDigest: string | undefined;
};
export const pauseJsonReply = async <K extends WebV2EndpointId>(
  page: Page,
  endpoint: K,
  drop = false,
) => {
  const path = webV2Endpoints.find((entry) => entry.id === endpoint)?.path;
  if (path === undefined) throw new Error("E2E_LOCALIZATION_ENDPOINT_MISSING");
  const ready = deferred<Captured<K>>();
  const released = deferred<void>();
  await page.route(
    `**${path}`,
    async (route) => {
      try {
        const response = await route.fetch();
        const payload: unknown = await response.json();
        if (response.status() !== 200 || !(await isWebV2Response(endpoint, payload)))
          throw new Error("E2E_LOCALIZATION_NATIVE_REPLY_INVALID");
        const bytes = route.request().postDataBuffer();
        if (bytes === null) throw new Error("E2E_LOCALIZATION_REQUEST_BYTES_MISSING");
        ready.resolve({
          reply: payload as WebV2Response<K>,
          bytes,
          sourceDigest: route.request().headers()["x-claimcore-source-sha256"],
        });
        await released.promise;
        if (drop) await route.abort("connectionfailed");
        else await route.fulfill({ response });
      } catch {
        ready.reject(new Error("E2E_LOCALIZATION_PAUSED_REQUEST_FAILED"));
      }
    },
    { times: 1 },
  );
  return { ready: ready.promise, release: () => released.resolve() };
};
export const inspectPending = async (page: Page, identity: PreparedIdentity): Promise<void> => {
  await page.getByRole("button", { name: "Recovery", exact: true }).click();
  const row = page.locator(".recovery-list li").filter({ hasText: identity.operationId });
  await expect(row).toHaveCount(1);
  await row.getByRole("button", { name: "Inspect", exact: true }).click();
  await expect(page.getByRole("dialog")).toContainText(identity.requestSha256);
};
export const noHorizontalOverflow = async (page: Page): Promise<void> => {
  const fits = await page.evaluate(
    () => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1,
  );
  expect(fits).toBe(true);
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
