import { pseudolocalizeLiteral } from "../src/presentation/templates";
import { showAccountActions } from "./workspace-navigation";
import { expect, test, type Page } from "./fixtures";
import { openApplication, openAuthenticated, seedSignedOut } from "./session-helpers";
import {
  expectPreferences,
  selectLanguage,
  trackForegroundRequests,
  ui,
  seedPresentation,
  openLabel,
} from "./localization-support";
import {
  displayLocales,
  languages,
  preferenceKey,
  type Language,
} from "../src/presentation/preferences";
import { endpoints, presentationLanguageOptions } from "@contracts/client";
import type { ServiceResponse } from "../src/protocol/types";
import type { CatalogLanguage } from "../src/presentation/languageCatalog";

const openPairPage = async (page: Page, source: Language, authenticated: boolean) => {
  if (!authenticated) {
    await seedSignedOut(page);
    await openApplication(
      page,
      source === "en-XA"
        ? `⟦${pseudolocalizeLiteral(ui("en", "notice.signedOutTitle"))}⟧`
        : ui(source, "notice.signedOutTitle"),
    );
    return;
  }
  const endpoint = endpoints.find((entry) => entry.id === "case.list");
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
      expect((payload as ServiceResponse<"case.list">).outcome.kind).toBe("SUCCEEDED");
    });
  await Promise.all([
    initialList,
    openAuthenticated(page, source === "en-XA" ? "⟦Cààsëës⟧" : ui(source, "label.cases")),
  ]);
  const list = page.locator('section[aria-labelledby="case-list-title"]');
  await expect(list.locator(':scope > p[role="status"]')).toHaveCount(0);
  await expect(list.locator(':scope > p[role="alert"]')).toHaveCount(0);
};
const visibleLanguage = async (page: Page, language: CatalogLanguage, authenticated: boolean) => {
  await expectPreferences(page, {
    language,
  });
  if (authenticated) {
    await expect(
      page.getByRole("heading", {
        name: ui(language, "label.cases"),
        exact: true,
        level: 1,
      }),
    ).toBeVisible();
    const open = page.getByRole("link", {
      name: openLabel(language),
      exact: true,
    });
    await expect(open).toBeVisible();
    await expect(open).toHaveAttribute("href", "/cases/new");
    await showAccountActions(page);
    await expect(
      page.getByRole("menuitem", {
        name: ui(language, "ui.signOut"),
        exact: true,
      }),
    ).toBeVisible();
    await page.keyboard.press("Escape");
  } else {
    await expect(
      page.getByRole("link", {
        name: ui(language, "label.signInAgain"),
        exact: true,
      }),
    ).toBeVisible();
    await expect(
      page.getByText(ui(language, "notice.signedOutTitle"), {
        exact: true,
      }),
    ).toBeVisible();
  }
};

// Every language enters and leaves a live document, and each display format is exercised once.
// Text and numeric-format preferences are independent, so crossing every pair adds no new policy path.
const realLanguages = presentationLanguageOptions.map((option) => option.value);
const transitions = displayLocales.map((displayLocale, index) => ({
  source: languages[index % languages.length]!,
  target: realLanguages[(index + 1) % realLanguages.length]!,
  displayLocale,
}));
for (const authenticated of [false, true]) {
  for (const { source, target, displayLocale } of transitions) {
    test(`updates visible ${authenticated ? "authenticated" : "anonymous"} text from ${source} to ${target} with ${displayLocale}`, async ({
      page,
    }) => {
      await seedPresentation(page, source, displayLocale);
      await openPairPage(page, source, authenticated);
      const requests = trackForegroundRequests(page);
      await selectLanguage(page, target);
      await visibleLanguage(page, target, authenticated);
      await expectPreferences(page, {
        displayLocale,
      });
      expect(
        await page.evaluate(
          (key): unknown => JSON.parse(localStorage.getItem(key) ?? "null"),
          preferenceKey,
        ),
      ).toEqual({
        version: 3,
        language: target,
        displayLocale,
        mode: "system",
        accent: "green",
      });
      expect(requests).toHaveLength(0);
    });
  }
}
