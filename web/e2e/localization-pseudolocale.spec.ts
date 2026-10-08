import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "@playwright/test";
import { openAuthenticated, expectAccessible } from "./session-helpers";
import {
  seedPresentation,
  selectLanguage,
  pauseJsonReply,
  preparedFrom,
  confirmPrepared,
  noHorizontalOverflow,
  trackRequests,
} from "./localization-support";

const startPseudo = async (page: Page) => {
  await seedPresentation(page, "en-XA", "ar-EG");
  await openAuthenticated(page, "⟦Cààsëës⟧");
  await page.locator('section[aria-labelledby="case-list-title"] .section-heading button').click();
  const values = {
    caseReference: `PSEUDO-${randomUUID()}`,
    incidentDate: "2026-09-01",
    incidentNotificationDate: "2026-09-02",
    incidentCountry: "Latvia",
    claimantName: "A\u0308 العربية\u200D",
    insurerName: "Synthetic insurer",
    claimedAmount: "1.0000",
    claimedCurrency: "EUR",
  };
  for (const [name, value] of Object.entries(values)) {
    await page.locator(`input[name="${name}"]`).fill(value);
  }
  const pending = await pauseJsonReply(page, "command.prepare");
  await page.locator('.operation-editor button[type="submit"]').click();
  const captured = await pending.ready;
  const identity = preparedFrom(captured.reply);
  pending.release();
  await expect(page.getByRole("dialog")).toContainText(identity.requestSha256);
  await confirmPrepared(page);
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
  await expect(dialog).toContainText(identity.requestSha256);
  for (const [name, value] of Object.entries(values)) {
    await expect(page.locator(`input[name="${name}"]`)).toHaveValue(value);
  }
  expect(requests).toHaveLength(0);
  await expect(page.locator('option[value="en-XA"]')).toHaveCount(0);
});

test("keeps expanded accepted-value layout and canonical clipboard on the normal published bytes", async ({
  page,
}) => {
  await startPseudo(page);
  await page.getByRole("dialog").locator(".dialog-actions button").last().click();
  await expect(page.locator("section.receipt")).toBeVisible();
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
  await amount.locator("details > summary").click();
  await amount.getByRole("button").click();
  await expect(amount.getByRole("textbox")).toHaveValue("1");
  const revision = await page.locator("section.receipt").textContent();
  await selectLanguage(page, "en");
  await expect(page.locator("section.receipt")).toContainText("Recorded change");
  expect(revision).not.toBeNull();
  await expect(amount.getByRole("textbox")).toHaveValue("1");
  await expect(page.locator('.field-row[data-field-name="claimedAmount"] bdi')).toHaveText("١");
});
