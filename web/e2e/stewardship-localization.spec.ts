import { showExactLookup } from "./workspace-navigation";
import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { openCase } from "./case-workflow";
import { openAuthenticated, expectAccessible } from "./session-helpers";
import {
  seedPresentation,
  selectLanguage,
  noHorizontalOverflow,
  expectKeyboardContained,
  ui,
} from "./localization-support";

test("stewardship review preserves exact reason through pseudolocale, Arabic RTL and narrow keyboard interaction", async ({
  page,
  browser,
}) => {
  await openAuthenticated(page);
  const reference = `STEWARD-LOCALE-${randomUUID()}`;
  await openCase(page, reference);
  const context = await browser.newContext({
    baseURL: new URL(page.url()).origin,
    ignoreHTTPSErrors: true,
  });
  try {
    const steward = await context.newPage();
    await seedPresentation(steward, "en-XA", "ar-EG");
    await openAuthenticated(steward, "⟦Cààsëës⟧", "narrow-authenticated-state.json");
    const lookup = await showExactLookup(steward);
    await lookup.fill(reference);
    await lookup.press("Enter");
    await steward.getByRole("button", { name: /Vööïïd/u }).click();
    const reason = "Exact 1.0000 العربية";
    await steward.locator('input[name="act.reason"]').fill(reason);
    const preview = steward.locator(".operation-editor button[type=submit]");
    await preview.click();
    const dialog = steward.getByRole("dialog");
    await expect(dialog).toContainText(reason);
    await expect(dialog.getByRole("heading", { name: /Vööïïd/u })).toBeVisible();
    await steward.setViewportSize({ width: 320, height: 720 });
    await noHorizontalOverflow(steward, "narrow");
    await expectKeyboardContained(steward, dialog);
    const original = await dialog.elementHandle();
    for (const language of ["ar", "lv", "en"] as const) {
      await selectLanguage(steward, language);
      expect(await dialog.evaluate((element, previous) => element === previous, original)).toBe(
        true,
      );
      await expect(dialog).toHaveAccessibleName(ui(language, "label.reviewTitle"));
      await expect(dialog).toContainText(reason);
      await noHorizontalOverflow(steward, "narrow");
    }
    await expect(dialog).toContainText("Before: Active");
    await expect(dialog).toContainText("After: Voided as a data-entry error");
    await expectAccessible(steward);
    await steward.keyboard.press("Escape");
    await expect(dialog).toHaveCount(0);
    await expect(preview).toBeFocused();
  } finally {
    await context.close();
  }
});
