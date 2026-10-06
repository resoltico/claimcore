import { expect, test, type Page } from "@playwright/test";
import { openAuthenticated } from "./session-helpers";
import { selectLanguage, trackRequests, ui } from "./localization-support";
import { displayLocales, languages, preferenceKey } from "../src/presentation/preferences";
import type { Language, DisplayLocale } from "../src/presentation/preferences";

const seed = async (page: Page, language: Language, displayLocale: DisplayLocale) => {
  await page.addInitScript(
    ({ key, preferences }) => {
      localStorage.setItem(key, JSON.stringify(preferences));
    },
    { key: preferenceKey, preferences: { version: 1, language, displayLocale } },
  );
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
      test(`updates visible ${authenticated ? "authenticated" : "anonymous"} text from ${source} to each real language with ${displayLocale}`, async ({
        page,
      }) => {
        for (const target of ["en", "lv", "ar"] as const) {
          const candidate = await page.context().newPage();
          try {
            await seed(candidate, source, displayLocale);
            // Seeded pseudolocale uses the supported parser on the same published bytes.
            if (authenticated) {
              await openAuthenticated(
                candidate,
                source === "en-XA" ? "⟦Cààsëës⟧" : ui(source, "ui.cases"),
              );
              await expect(
                candidate.locator('section[aria-labelledby="case-list-title"] > p[role="status"]'),
              ).toHaveCount(0);
            } else {
              await candidate.goto("/");
              await expect(candidate.getByRole("heading", { name: "ClaimCore" })).toBeVisible();
            }
            const requests = trackRequests(candidate);
            await selectLanguage(candidate, target);
            await visibleLanguage(candidate, target, authenticated);
            await expect(candidate.locator('select[id$="-format"]')).toHaveValue(displayLocale);
            expect(
              await candidate.evaluate(
                (key): unknown => JSON.parse(localStorage.getItem(key) ?? "null"),
                preferenceKey,
              ),
            ).toEqual({ version: 1, language: target, displayLocale });
            expect(requests).toHaveLength(0);
          } finally {
            await candidate.close();
          }
        }
      });
    }
  }
}
