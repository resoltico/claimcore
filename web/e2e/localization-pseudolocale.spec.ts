import { fillBusinessField } from "./typed-entry";
import { registration } from "../tests/support/inputs";
import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "@playwright/test";
import { openAuthenticated, expectAccessible } from "./session-helpers";
import {
  seedPresentation,
  selectLanguage,
  pauseJsonReply,
  reviewFrom,
  confirmReview,
  noHorizontalOverflow,
  trackRequests,
} from "./localization-support";

const startPseudo = async (page: Page) => {
  await seedPresentation(page, "en-XA", "ar-EG");
  await openAuthenticated(page, "⟦Cààsëës⟧");
  await page.locator('section[aria-labelledby="case-list-title"] .section-heading button').click();
  const values = {
    caseReference: `PSEUDO-${randomUUID()}`,
    ...registration({ claimantName: "A\u0308 العربية\u200D", claimedAmount: "1.0000" }),
  };
  for (const [name, value] of Object.entries(values)) {
    await fillBusinessField(page, name, value);
  }
  const pending = await pauseJsonReply(page, "operation.preview");
  await page.locator('.operation-editor button[type="submit"]').click();
  const captured = await pending.ready;
  const identity = reviewFrom(captured.reply, captured.bytes);
  pending.release();

  await confirmReview(page);
  return { values, identity };
};

test("authors and prepares in seeded pseudolocale then switches out with draft, identity and consent intact", async ({
  page,
}) => {
  const { values, identity } = await startPseudo(page);
  const dialog = page.getByRole("dialog");
  const original = await dialog.elementHandle();
  const requests = trackRequests(page);
  await page.setViewportSize({ width: 320, height: 720 });
  await noHorizontalOverflow(page, "narrow");
  await expectAccessible(page);
  await selectLanguage(page, "ar");
  expect(await dialog.evaluate((element, previous) => element === previous, original)).toBe(true);
  await expect(dialog.getByRole("checkbox")).toBeChecked();
  await expect(dialog).toContainText(identity.operationId);

  for (const [name, value] of Object.entries(values)) {
    const field = page.locator(`[data-field-name="${name}"]`);
    const segments = field.getByRole("spinbutton");
    await expect(
      (await segments.count()) > 0 ? field.locator('input[type="hidden"]') : field,
    ).toHaveValue(value);
  }
  expect(requests).toHaveLength(0);
  await expect(page.locator('option[value="en-XA"]')).toHaveCount(0);
});

test("keeps expanded accepted-value layout and canonical clipboard on the normal published bytes", async ({
  page,
}) => {
  await startPseudo(page);
  await page
    .getByRole("dialog")
    .getByRole("button", { name: "⟦Rëëcöörd chààngëës⟧", exact: true })
    .click();
  await expect(page.locator("section.case-detail")).toBeVisible();
  await expect(page.locator(".success-toast")).toBeVisible();
  await page.setViewportSize({ width: 320, height: 720 });
  await noHorizontalOverflow(page, "narrow");
  await expectAccessible(page);
  await page.setViewportSize({ width: 640, height: 900 });
  await page.evaluate(() => {
    document.documentElement.style.zoom = "2";
  });
  await noHorizontalOverflow(page, "zoom");
  await page.evaluate(() =>
    Object.defineProperty(navigator, "clipboard", {
      configurable: true,
      value: { writeText: () => Promise.reject(new Error("Synthetic clipboard denial")) },
    }),
  );
  const amount = page.locator('.field-row[data-field-name="claimedAmount"]');
  const group = page.locator(".case-fact-card").filter({ has: amount });
  await group.locator(".explain-fields").click();
  await expect(group.locator(".explain-fields")).toHaveAttribute("aria-pressed", "true");
  await amount.getByRole("button").focus();
  await amount.getByRole("button").click();
  await expect(amount.getByRole("textbox")).toHaveValue("1");
  const revision = await page.locator(".success-toast").textContent();
  await selectLanguage(page, "en");
  await expect(page.locator(".success-toast")).toContainText("Open a case recorded at revision ١.");
  expect(revision).not.toBeNull();
  await expect(amount.getByRole("textbox")).toHaveValue("1");
  await expect(page.locator('.field-row[data-field-name="claimedAmount"] bdi')).toHaveText("١٫٠٠");
});
