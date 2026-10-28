import { randomUUID } from "node:crypto";
import { expect, test, type Page, type BrowserContext } from "@playwright/test";
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
    kind: "AUTHORITY",
    operationId: randomUUID(),
    target: { kind: "HUMAN", issuer: source.issuer, subject: source.stewardSubject },
    act: { kind: active ? "GRANT" : "REVOKE", role: "OWNER", scope: { kind: "INSTALLATION" } },
  });
};
const prepareRegistration = async (page: Page, issuer: string, subject: string) => {
  await page.locator('[name="target.kind"]').selectOption("SERVICE");
  await page.locator('[name="target.issuer"]').fill(issuer);
  await page.locator('[name="target.subject"]').fill(subject);
  await page
    .locator('section[aria-labelledby="authority-title"] form button[type="submit"]')
    .click();
  const prepared = page.getByRole("dialog");
  await expect(prepared).toContainText(subject);
  await page.getByRole("checkbox").check();
};
const uncertainRegistration = async (page: Page) => {
  const pending = await pauseJsonReply(page, "operation.execute", true);
  await page.getByRole("button", { name: ui("en", "ui.submitExact"), exact: true }).click();
  const captured = await pending.ready;
  expect(captured.reply.outcome.kind).toBe("ACCEPTED");
  pending.release();
  await expect(
    page.getByRole("button", { name: ui("en", "ui.retryExact"), exact: true }),
  ).toBeVisible();
  return captured.bytes;
};
const refusedRetry = async (page: Page, bytes: Buffer) => {
  const pending = await pauseJsonReply(page, "operation.execute");
  await page.getByRole("button", { name: ui("en", "ui.retryExact"), exact: true }).click();
  const captured = await pending.ready;
  expect(captured.bytes.equals(bytes)).toBe(true);
  expect(captured.reply.outcome.kind).toBe("REJECTED");
  pending.release();
  await expect(
    page.getByRole("button", { name: ui("en", "ui.retryExact"), exact: true }),
  ).toBeEnabled();
  await expect(page.locator(".authority-panel ul > li")).toHaveCount(0);
};

const cleanupAuthority = async (
  page: Page,
  context: BrowserContext,
  primary: Error | undefined,
) => {
  const outcomes = await Promise.allSettled([ownerGrant(page, false), context.close()]);
  const failures = outcomes.flatMap((outcome) => {
    if (outcome.status === "fulfilled") {
      return [];
    }
    const reason: unknown = outcome.reason;
    return [reason];
  });
  if (failures.length > 0) {
    test.info().annotations.push({
      type: "cleanup-failure",
      description: `${failures.length} authority cleanup actions failed.`,
    });
  }
  if (primary !== undefined) {
    throw primary;
  }
  if (failures.length > 0) {
    throw new AggregateError(failures, "Authority cleanup failed.");
  }
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
  let primary: Error | undefined;
  try {
    const steward = await context.newPage();
    await seedPresentation(steward, "en-XA", "ar-EG");
    await openAuthenticated(steward, "⟦Cààsëës⟧", "narrow-authenticated-state.json");
    const subject = `authority-uncertain-${randomUUID()}`;
    await steward.getByRole("link").and(steward.locator('nav a[href="/authority"]')).click();
    await expect(steward.locator("#authority-actors-title").locator("..")).toContainText(
      source.ownerSubject,
    );
    await prepareRegistration(steward, source.issuer, subject);
    await steward.setViewportSize({ width: 320, height: 720 });
    await noHorizontalOverflow(steward, "narrow");
    for (const language of ["ar", "lv", "en"] as const) {
      await selectLanguage(steward, language);
      await expect(steward.getByRole("checkbox")).toBeChecked();
      await expect(steward.getByRole("dialog")).toContainText(subject);
      await noHorizontalOverflow(steward, "narrow");
    }
    const bytes = await uncertainRegistration(steward);
    await ownerGrant(page, false);
    await refusedRetry(steward, bytes);
    await expect(steward.locator('section[aria-labelledby="authority-title"]')).not.toContainText(
      subject,
    );
    await expectAccessible(steward);
  } catch (error) {
    primary =
      error instanceof Error ? error : new Error("Authority workflow failed.", { cause: error });
  } finally {
    await cleanupAuthority(page, context, primary);
  }
});
