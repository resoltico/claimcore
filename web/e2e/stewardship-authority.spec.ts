import { goToView, showExactLookup, chooseChoice } from "./workspace-navigation";
import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "@playwright/test";
import { isResponse } from "../src/protocol/client";
import { openCase, startAct } from "./case-workflow";
import {
  expectAccessible,
  openAuthenticated,
  browserRequest,
  sessionToken,
} from "./session-helpers";
import { confirmReview } from "./localization-support";
import { syntheticPrincipals } from "./authority-setup";

const stewardAct = async (page: Page, label: string, reason?: string) => {
  await startAct(page, label);
  if (reason !== undefined) {
    await page.getByLabel("Stewardship reason", { exact: true }).fill(reason);
  }
  if (label === "Erase case payload") {
    await expect(page.getByText(/WAL, backups and copies elsewhere are not erased/u)).toBeVisible();
  }
  await page.getByRole("button", { name: "Review changes", exact: true }).click();
  await confirmReview(page);
  const pending = page.waitForResponse((reply) => reply.url().endsWith("/api/operation.execute"));
  await page.getByRole("button", { name: "Record changes", exact: true }).click();
  const reply = await pending;
  const value: unknown = await reply.json();
  expect(isResponse("operation.execute", value)).toBe(true);
  if (!isResponse("operation.execute", value)) {
    throw new Error("Missing stewardship response.");
  }
  expect(value.outcome.kind).toBe("ACCEPTED");
  await expect(page.getByRole("heading", { name: "Case detail", exact: true })).toBeVisible();
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
    await (await showExactLookup(steward)).fill(reference);
    await steward.getByRole("button", { name: "Open by exact reference", exact: true }).click();
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
  await chooseChoice(page, "target.kind", "SERVICE");
  await page.locator('[name="target.issuer"]').fill(issuer);
  await page.locator('[name="target.subject"]').fill(subject);
};
const submitAuthority = async (page: Page, expected: "ACCEPTED" | "REJECTED") => {
  const previewed = page.waitForResponse((reply) => reply.url().endsWith("/api/operation.preview"));
  await page.getByRole("button", { name: "Review changes", exact: true }).click();
  const previewResponse = await previewed;
  const preview: unknown = await previewResponse.json();
  if (!isResponse("operation.preview", preview)) {
    throw new Error("Missing authority preview.");
  }
  expect(preview.outcome.kind).toBe(expected === "ACCEPTED" ? "REVIEWED" : "REJECTED");
  if (expected === "REJECTED") {
    await expect(page.getByRole("dialog")).toHaveCount(0);
    return;
  }
  if (
    preview.outcome.kind !== "REVIEWED" ||
    preview.outcome.data.review.subject.kind !== "AUTHORITY"
  ) {
    throw new Error("Expected authority review.");
  }
  await page.getByRole("checkbox", { name: "I confirm this exact request.", exact: true }).check();
  const pending = page.waitForResponse((reply) => reply.url().endsWith("/api/operation.execute"));
  await page.getByRole("button", { name: "Record changes", exact: true }).click();
  const reply = await pending;
  expect(reply.request().postData()).toBe(previewResponse.request().postData());
  const value: unknown = await reply.json();
  if (!isResponse("operation.execute", value)) {
    throw new Error("Missing authority response.");
  }
  expect(value.outcome.kind).toBe("ACCEPTED");
  if (value.outcome.kind === "ACCEPTED") {
    await expect(
      page.getByRole("status").filter({ hasText: value.outcome.data.operationId }),
    ).toBeVisible();
    await page.getByRole("button", { name: "Start another operation", exact: true }).click();
  }
};
const selectAuthorityAct = async (page: Page, act: "GRANT" | "DISABLE") => {
  const picker = page.getByRole("combobox", { name: "Authority action", exact: true });
  const option = picker.locator(`option[value="${act}"]`);
  await expect(picker).toBeEnabled();
  await expect(option).toHaveCount(1);
  await expect(option).toBeEnabled();
  await picker.selectOption(act);
};
const assertHumanOnlyBoundary = async (page: Page, issuer: string, subject: string) => {
  const response = await browserRequest(page, "/api/operation.preview", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-ClaimCore-Antiforgery": await sessionToken(page),
    },
    body: JSON.stringify({
      kind: "AUTHORITY",
      operationId: randomUUID(),
      target: { kind: "SERVICE", issuer, subject },
      act: { kind: "GRANT", role: "OWNER", scope: { kind: "INSTALLATION" } },
    }),
  });
  const value = response.payload;
  expect(isResponse("operation.preview", value)).toBe(true);
  expect(value).toMatchObject({
    outcome: { kind: "REJECTED", data: { code: "HUMAN_PRINCIPAL_REQUIRED" } },
  });
};
test("browser registers service principals, changes scoped grants and enablement, and renders core refusal", async ({
  page,
}) => {
  await openAuthenticated(page);
  const source = await syntheticPrincipals();
  const reference = `AUTHORITY-${randomUUID()}`;
  await openCase(page, reference);
  await goToView(page, "authority");
  const subject = `browser-service-${randomUUID()}`;
  await authorityTarget(page, source.issuer, subject);
  await submitAuthority(page, "ACCEPTED");
  await selectAuthorityAct(page, "GRANT");
  await authorityTarget(page, source.issuer, subject);
  await chooseChoice(page, "act.role", "CASE_READER");
  await chooseChoice(page, "act.scope.kind", "CASE");
  await page.locator('[name="act.scope.caseReference"]').fill(reference);
  await submitAuthority(page, "ACCEPTED");
  await selectAuthorityAct(page, "DISABLE");
  await authorityTarget(page, source.issuer, subject);
  await submitAuthority(page, "ACCEPTED");
  await selectAuthorityAct(page, "GRANT");
  await authorityTarget(page, source.issuer, subject);
  const owner = page.locator('input[name="act.role"][value="OWNER"]');
  await expect(owner).toBeDisabled();
  await expect(owner).toHaveAccessibleDescription(/Not available for service principals/u);
  await assertHumanOnlyBoundary(page, source.issuer, subject);
  await page.locator('[name="target.subject"]').fill(`unregistered-${randomUUID()}`);
  await chooseChoice(page, "act.role", "CASE_READER");
  await submitAuthority(page, "REJECTED");
  await expect(page.getByRole("alert")).toBeVisible();
  await expectAccessible(page);
});
