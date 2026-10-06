import { expect, test, type Page } from "@playwright/test";
import { openAuthenticated } from "./session-helpers";
import { selectLanguage, trackRequests, ui, seedPresentation } from "./localization-support";
import { displayLocales, languages, preferenceKey } from "../src/presentation/preferences";

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
      page.getByRole("button", { name: ui(language, "ui.openNewCase"), exact: true }),
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
          if (authenticated) {
            await openAuthenticated(
              page,
              source === "en-XA" ? "⟦Cààsëës⟧" : ui(source, "ui.cases"),
            );
            await expect(
              page.locator('section[aria-labelledby="case-list-title"] > p[role="status"]'),
            ).toHaveCount(0);
          } else {
            await page.goto("/", { waitUntil: "domcontentloaded" });
            await expect(page.getByRole("heading", { name: "ClaimCore" })).toBeVisible();
          }
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
