import { expect, test, type Page } from "@playwright/test";
import { openApplication, openAuthenticated } from "./session-helpers";
import {
  selectLanguage,
  trackRequests,
  ui,
  seedPresentation,
  openLabel,
} from "./localization-support";
import { displayLocales, languages, preferenceKey } from "../src/presentation/preferences";
import { webV3Endpoints } from "../src/generated/contracts/web-v3.endpoint-catalog";
import { isWebV3Response } from "../src/generated/contracts/web-v3.validation";
import type { WebV3Response } from "../src/generated/contracts/web-v3.types";
import type { Language } from "../src/presentation/preferences";

const openPairPage = async (page: Page, source: Language, authenticated: boolean) => {
  if (!authenticated) {
    await openApplication(page, "ClaimCore");
    return;
  }
  const endpoint = webV3Endpoints.find((entry) => entry.id === "case.list");
  if (endpoint === undefined) {
    throw new Error("E2E_CASE_LIST_ENDPOINT_MISSING");
  }
  const initialList = page
    .waitForResponse(
      (reply) =>
        new URL(reply.url()).pathname === endpoint.path &&
        reply.request().method() === endpoint.method,
    )
    .then(async (reply) => {
      expect(reply.status()).toBe(200);
      const payload: unknown = await reply.json();
      expect(await isWebV3Response("case.list", payload)).toBe(true);
      expect((payload as WebV3Response<"case.list">).outcome.tag).toBe("SUCCEEDED");
    });
  await Promise.all([
    initialList,
    openAuthenticated(page, source === "en-XA" ? "⟦Cààsëës⟧" : ui(source, "ui.cases")),
  ]);
  const list = page.locator('section[aria-labelledby="case-list-title"]');
  await expect(list.locator(':scope > p[role="status"]')).toHaveCount(0);
  await expect(list.locator(':scope > p[role="alert"]')).toHaveCount(0);
};

const visibleLanguage = async (
  page: Page,
  language: "en" | "lv" | "ar",
  authenticated: boolean,
) => {
  await expect(page.getByRole("combobox").and(page.locator('select[id$="-language"]'))).toHaveValue(
    language,
  );
  await expect(page.getByText(ui(language, "ui.preferencesHint"), { exact: true })).toBeVisible();
  if (authenticated) {
    await expect(
      page.getByRole("heading", { name: ui(language, "ui.cases"), exact: true }),
    ).toBeVisible();
    await expect(
      page.getByRole("button", {
        name: openLabel(language),
        exact: true,
      }),
    ).toBeVisible();
    await expect(
      page.getByRole("button", { name: ui(language, "ui.signOut"), exact: true }),
    ).toBeVisible();
  } else {
    await expect(
      page.getByRole("link", { name: ui(language, "ui.signIn"), exact: true }),
    ).toBeVisible();
    await expect(
      page.getByText(ui(language, "ui.loginInstructions"), { exact: true }),
    ).toBeVisible();
  }
};

for (const authenticated of [false, true]) {
  for (const displayLocale of displayLocales) {
    for (const source of languages) {
      for (const target of ["en", "lv", "ar"] as const) {
        test(`updates visible ${authenticated ? "authenticated" : "anonymous"} text from ${source} to ${target} with ${displayLocale}`, async ({
          page,
        }) => {
          await seedPresentation(page, source, displayLocale);
          await openPairPage(page, source, authenticated);
          const requests = trackRequests(page);
          await selectLanguage(page, target);
          await visibleLanguage(page, target, authenticated);
          await expect(page.locator('select[id$="-format"]')).toHaveValue(displayLocale);
          expect(
            await page.evaluate(
              (key): unknown => JSON.parse(localStorage.getItem(key) ?? "null"),
              preferenceKey,
            ),
          ).toEqual({ version: 1, language: target, displayLocale });
          expect(requests).toHaveLength(0);
        });
      }
    }
  }
}
