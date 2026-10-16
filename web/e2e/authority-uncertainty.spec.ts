import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "@playwright/test";
import { applyAuthority, syntheticPrincipals } from "./authority-setup";
import { openAuthenticated, expectAccessible } from "./session-helpers";
import {
  seedPresentation,
  selectLanguage,
  pauseJsonReply,
  noHorizontalOverflow,
  ui,
} from "./localization-support";

const ownerGrant = async (page: Page, active: boolean) => {
  const source = await syntheticPrincipals();
  await applyAuthority(page, {
    operationId: randomUUID(),
    target: { kind: "HUMAN", issuer: source.issuer, subject: source.stewardSubject },
    act: { kind: "SET_GRANT", role: "OWNER", scope: { kind: "INSTALLATION" }, active },
  });
};
const prepareRegistration = async (page: Page, issuer: string, subject: string) => {
  await page.locator("nav button").last().click();
  await page.locator('[name="target.kind"]').selectOption("SERVICE");
  await page.locator('[name="target.issuer"]').fill(issuer);
  await page.locator('[name="target.subject"]').fill(subject);
  await page.locator('section[aria-labelledby="authority-title"] form button[type=submit]').click();
  const prepared = page.locator('section[aria-labelledby="authority-title"] > section').first();
  await expect(prepared).toContainText(subject);
  await page.getByRole("checkbox").check();
};
const uncertainRegistration = async (page: Page) => {
  const pending = await pauseJsonReply(page, "authority.execute", true);
  await page.getByRole("button", { name: ui("en", "ui.submitOperation"), exact: true }).click();
  const captured = await pending.ready;
  expect(captured.reply.outcome.tag).toBe("ACCEPTED");
  pending.release();
  await expect(
    page.getByRole("button", { name: ui("en", "ui.retryExact"), exact: true }),
  ).toBeVisible();
  return captured.bytes;
};
const refusedRetry = async (page: Page, bytes: Buffer) => {
  const pending = await pauseJsonReply(page, "authority.execute");
  await page.getByRole("button", { name: ui("en", "ui.retryExact"), exact: true }).click();
  const captured = await pending.ready;
  expect(captured.bytes.equals(bytes)).toBe(true);
  expect(captured.reply.outcome.tag).toBe("REJECTED");
  pending.release();
  await expect(
    page.getByRole("button", { name: ui("en", "ui.retryExact"), exact: true }),
  ).toBeEnabled();
  const listing = page
    .getByRole("heading", { name: ui("en", "ui.actors"), exact: true })
    .locator("..");
  await expect(listing.locator("ul > li")).toHaveCount(0);
};

test("authority review and uncertainty survive locale changes while refusal clears prior actor disclosures", async ({
  page,
  browser,
}) => {
  await openAuthenticated(page);
  const source = await syntheticPrincipals();
  await ownerGrant(page, true);
  const context = await browser.newContext({
    baseURL: new URL(page.url()).origin,
    ignoreHTTPSErrors: true,
  });
  try {
    const steward = await context.newPage();
    await seedPresentation(steward, "en-XA", "ar-EG");
    await openAuthenticated(steward, "⟦Cààsëës⟧", "narrow-authenticated-state.json");
    const subject = `authority-uncertain-${randomUUID()}`;
    await prepareRegistration(steward, source.issuer, subject);
    await steward.setViewportSize({ width: 320, height: 720 });
    await noHorizontalOverflow(steward, "narrow");
    for (const language of ["ar", "lv", "en"] as const) {
      await selectLanguage(steward, language);
      await expect(steward.getByRole("checkbox")).toBeChecked();
      await expect(steward.locator('section[aria-labelledby="authority-title"]')).toContainText(
        subject,
      );
      await noHorizontalOverflow(steward, "narrow");
    }
    await expect(
      steward.getByRole("heading", { name: ui("en", "ui.actors"), exact: true }).locator(".."),
    ).toContainText(source.ownerSubject);
    const bytes = await uncertainRegistration(steward);
    await ownerGrant(page, false);
    await refusedRetry(steward, bytes);
    await expect(steward.locator('section[aria-labelledby="authority-title"]')).toContainText(
      subject,
    );
    await expectAccessible(steward);
  } finally {
    try {
      await ownerGrant(page, false);
    } finally {
      await context.close();
    }
  }
});
