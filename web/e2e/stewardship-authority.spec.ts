import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "@playwright/test";
import { isResponse } from "../src/protocol/client";
import { openCase, startCommand } from "./case-workflow";
import { expectAccessible, openAuthenticated } from "./session-helpers";
import { confirmReview } from "./localization-support";
import { syntheticPrincipals } from "./authority-setup";

const stewardAct = async (page: Page, label: string, reason?: string) => {
  await startCommand(page, label);
  if (reason !== undefined) {
    await page.getByLabel("Stewardship reason", { exact: true }).fill(reason);
  }
  if (label === "Erase case payload") {
    await expect(page.getByText(/WAL, backups and copies elsewhere are not erased/u)).toBeVisible();
  }
  await page.getByRole("button", { name: "Review changes", exact: true }).click();
  await confirmReview(page);
  const pending = page.waitForResponse((reply) => reply.url().endsWith("/api/case.execute"));
  await page.getByRole("button", { name: "Record changes", exact: true }).click();
  const reply = await pending;
  const value: unknown = await reply.json();
  expect(isResponse("case.execute", value)).toBe(true);
  if (!isResponse("case.execute", value)) {
    throw new Error("Missing stewardship response.");
  }
  expect(value.outcome.tag).toBe("ACCEPTED");
  await page.getByRole("button", { name: "Return to case", exact: true }).click();
};
test("browser renders void, reinstatement and live payload erasure through authoritative preview", async ({
  page,
  browser,
}) => {
  await openAuthenticated(page);
  const reference = `STEWARD-${randomUUID()}`;
  await openCase(page, reference);
  const context = await browser.newContext({
    baseURL: new URL(page.url()).origin,
    ignoreHTTPSErrors: true,
  });
  try {
    const steward = await context.newPage();
    await openAuthenticated(steward, "Cases", "narrow-authenticated-state.json");
    await steward.getByLabel("Handler's case reference (exact)").fill(reference);
    await steward.getByRole("button", { name: "Find case", exact: true }).click();
    await stewardAct(steward, "Void case record", "Synthetic entry error");
    await expect(steward.getByRole("status").filter({ hasText: "Voided" })).toBeVisible();
    await stewardAct(steward, "Reinstate case record", "Synthetic correction");
    await stewardAct(steward, "Erase case payload");
    await expect(steward.getByText(/Erased — only/u)).toBeVisible();
    await expect(steward.locator(".case-fields")).toHaveCount(0);
    await expectAccessible(steward);
  } finally {
    await context.close();
  }
});
const authorityTarget = async (page: Page, issuer: string, subject: string) => {
  await page.locator('[name="target.kind"]').selectOption("SERVICE");
  await page.locator('[name="target.issuer"]').fill(issuer);
  await page.locator('[name="target.subject"]').fill(subject);
};
const submitAuthority = async (page: Page, expected: "ACCEPTED" | "REJECTED") => {
  await page.getByRole("button", { name: "Review changes", exact: true }).click();
  await page.getByRole("checkbox", { name: "I confirm this exact request.", exact: true }).check();
  const pending = page.waitForResponse((reply) => reply.url().endsWith("/api/authority.execute"));
  await page.getByRole("button", { name: "Submit operation", exact: true }).click();
  const reply = await pending;
  const value: unknown = await reply.json();
  expect(isResponse("authority.execute", value)).toBe(true);
  if (!isResponse("authority.execute", value)) {
    throw new Error("Missing authority response.");
  }
  expect(value.outcome.tag).toBe(expected);
  if (value.outcome.tag === "ACCEPTED" || value.outcome.tag === "ALREADY_ACCEPTED") {
    await expect(
      page.getByRole("status").filter({ hasText: value.outcome.data.operationId }),
    ).toBeVisible();
  }
};
const selectAuthorityAct = async (page: Page, act: "SET_GRANT" | "SET_ENABLED") => {
  const picker = page.getByRole("combobox", { name: "Authority action", exact: true });
  const option = picker.locator(`option[value="${act}"]`);
  await expect(picker).toBeEnabled();
  await expect(option).toHaveCount(1);
  await expect(option).toBeEnabled();
  await picker.selectOption(act);
};
test("browser registers service principals, changes scoped grants and enablement, and renders core refusal", async ({
  page,
}) => {
  await openAuthenticated(page);
  const source = await syntheticPrincipals();
  const reference = `AUTHORITY-${randomUUID()}`;
  await openCase(page, reference);
  await page.getByRole("button", { name: "Authority", exact: true }).click();
  const subject = `browser-service-${randomUUID()}`;
  await authorityTarget(page, source.issuer, subject);
  await submitAuthority(page, "ACCEPTED");
  await selectAuthorityAct(page, "SET_GRANT");
  await authorityTarget(page, source.issuer, subject);
  await page.locator('[name="act.role"]').selectOption("CASE_READER");
  await page.locator('[name="act.scope.kind"]').selectOption("CASE");
  await page.locator('[name="act.scope.caseReference"]').fill(reference);
  await page.locator('[name="act.active"]').check();
  await submitAuthority(page, "ACCEPTED");
  await selectAuthorityAct(page, "SET_ENABLED");
  await authorityTarget(page, source.issuer, subject);
  await page.locator('[name="act.enabled"]').uncheck();
  await submitAuthority(page, "ACCEPTED");
  await selectAuthorityAct(page, "SET_GRANT");
  await authorityTarget(page, source.issuer, subject);
  await page.locator('[name="act.role"]').selectOption("OWNER");
  await page.locator('[name="act.active"]').check();
  await submitAuthority(page, "REJECTED");
  await expect(page.getByRole("alert")).toBeVisible();
  await expectAccessible(page);
});
