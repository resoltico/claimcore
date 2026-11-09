import { expectCaseReadsSettled } from "./case-readiness";
import { operationId as mintOperationId } from "../src/operation/operationId";
import type { CaseAct, AuthorityAct } from "@contracts/client";
import { freshLoginWorkflowTimeout } from "./admission-window";
import type { Language } from "../src/presentation/preferences";
import { defaults } from "../src/presentation/preferences";
import { createPresenter } from "../src/presentation/presenter";
import { goToView, showExactLookup, chooseChoice } from "./workspace-navigation";
import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "@playwright/test";
import { isResponse } from "../src/protocol/client";
import { openCase, startAct, recordReviewed } from "./case-workflow";
import {
  expectAccessible,
  openAuthenticated,
  authenticateSteward,
  browserRequest,
  sessionToken,
} from "./session-helpers";
import { confirmReview } from "./localization-support";
import { syntheticPrincipals } from "./authority-setup";
import { recoveryRecords } from "./recovery-storage";

let stewardCookies: Awaited<ReturnType<typeof authenticateSteward>>;
test.beforeAll(async ({ browser, baseURL }) => {
  test.setTimeout(freshLoginWorkflowTimeout);
  stewardCookies = await authenticateSteward(browser, baseURL);
});

const stewardAct = async (
  page: Page,
  reference: string,
  kind: CaseAct["kind"],
  reason?: string,
) => {
  const p = createPresenter({ ...defaults, language: "en" });
  const label = p.actLabel(kind);
  await startAct(page, kind, "en");
  const dialog = page.getByRole("dialog", { name: label, exact: true });
  if (reason !== undefined) {
    await dialog.getByLabel("Reason", { exact: true }).fill(reason);
  }
  if (kind === "ERASE") {
    await expect(dialog).toContainText("Copies held elsewhere are not affected.");
  }
  const previewed = page.waitForResponse(
    (reply) => new URL(reply.url()).pathname === "/api/operation.preview",
  );
  await dialog.getByRole("button", { name: "Review changes", exact: true }).click();
  const preview = await previewed;
  await confirmReview(page);
  const action = dialog.locator("button[data-confirm-operation]");
  if (kind === "ERASE") {
    await expect(dialog).toContainText(
      "This permanently removes the case details, work and reminders from ClaimCore.",
    );
    const challenge = dialog.getByRole("textbox", { name: "Case reference (exact)", exact: true });
    await expect(action).toBeDisabled();
    await challenge.fill(`${reference}-WRONG`);
    await expect(action).toBeDisabled();
    await challenge.fill(reference);
  }
  await expect(action).toBeEnabled();
  const pending = page.waitForResponse(
    (reply) => new URL(reply.url()).pathname === "/api/operation.execute",
  );
  await recordReviewed(page);
  const reply = await pending;
  expect(reply.request().postData()).toBe(preview.request().postData());
  const value: unknown = await reply.json();
  if (!isResponse("operation.execute", value)) {
    throw new Error("Missing stewardship response.");
  }
  expect(value.outcome.kind).toBe("ACCEPTED");
  if (value.outcome.kind === "ACCEPTED") {
    expect(value.outcome.data.subject).toMatchObject({ kind: "CASE", caseReference: reference });
  }
  await expect(page.locator("#case-detail-title")).toBeVisible();
  await expectCaseReadsSettled(page);
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
    await openAuthenticated(steward, "Cases", stewardCookies);
    await (await showExactLookup(steward)).fill(reference);
    await steward.getByRole("button", { name: "Open by exact reference", exact: true }).click();
    await stewardAct(steward, reference, "VOID", "Synthetic entry error");
    await expect(
      steward.locator(
        '.case-header-state .code-value[data-vocabulary="lifecycle"][data-code="VOIDED"]',
      ),
    ).toContainText("Voided");
    await stewardAct(steward, reference, "REINSTATE", "Synthetic correction");
    await stewardAct(steward, reference, "ERASE");
    await expect(steward.locator('.region-notice[data-tone="FINAL"]')).toContainText(
      "Erased. Only",
    );
    await expect(steward.locator(".entries-view [data-entry='claimantName']")).toHaveCount(0);
    const history = steward.locator(".case-timeline-panel");
    for (const summary of await history.locator("details > summary").all()) {
      await summary.click();
    }
    await expect(history).not.toContainText("Synthetic claimant");
    await expect(history).not.toContainText("Synthetic insurer");
    await expect(history.locator('[data-entry$=".before"], [data-entry$=".after"]')).toHaveCount(0);
    await expectAccessible(steward);
  } finally {
    await context.close();
  }
});
const authorityTarget = async (page: Page, issuer: string, subject: string) => {
  await chooseChoice(page, "target.kind", "SERVICE");
  await expect(page.locator('[name="target.issuer"]')).toHaveValue(issuer);
  await expect(page.locator('[name="target.issuer"]')).toBeDisabled();
  await page.locator('[name="target.subject"]').fill(subject);
};
const submitAuthority = async (page: Page, expected: "ACCEPTED" | "REJECTED") => {
  const dialog = page.getByRole("dialog");
  const previewed = page.waitForResponse(
    (reply) => new URL(reply.url()).pathname === "/api/operation.preview",
  );
  await dialog.getByRole("button", { name: "Review changes", exact: true }).click();
  const previewResponse = await previewed;
  const preview: unknown = await previewResponse.json();
  if (!isResponse("operation.preview", preview)) {
    throw new Error("Missing authority preview.");
  }
  expect(preview.outcome.kind).toBe(expected === "ACCEPTED" ? "REVIEWED" : "REJECTED");
  if (expected === "REJECTED") {
    await expect(dialog).toBeVisible();
    await expect(dialog.getByRole("alert")).toBeVisible();
    return null;
  }
  if (
    preview.outcome.kind !== "REVIEWED" ||
    preview.outcome.data.review.subject.kind !== "AUTHORITY"
  ) {
    throw new Error("Expected authority review.");
  }
  await confirmReview(page);
  const pending = page.waitForResponse(
    (reply) => new URL(reply.url()).pathname === "/api/operation.execute",
  );
  await dialog.locator("button[data-confirm-operation]").click();
  const reply = await pending;
  expect(reply.request().postData()).toBe(previewResponse.request().postData());
  const value: unknown = await reply.json();
  if (
    !isResponse("operation.execute", value) ||
    value.outcome.kind !== "ACCEPTED" ||
    value.outcome.data.subject.kind !== "AUTHORITY"
  ) {
    throw new Error("Missing accepted authority response.");
  }
  await expect(
    page
      .getByRole("status")
      .filter({ hasText: new RegExp(`^${value.outcome.data.operationId}$`, "u") }),
  ).toBeVisible();
  await expect.poll(() => recoveryRecords(page)).toEqual([]);
  await expect(page.getByRole("dialog")).toHaveCount(0);
  return value.outcome.data;
};
const openAdmission = async (
  page: Page,
  issuer: string,
  subject: string,
  name: string,
  reference: string,
) => {
  await page.getByRole("button", { name: "Add person", exact: true }).first().click();
  const dialog = page.getByRole("dialog", { name: "Add person", exact: true });
  await authorityTarget(page, issuer, subject);
  await dialog.locator('[name="act.profile.displayName"]').fill(name);
  const grants = dialog.locator('fieldset[id="field-act.grants"]');
  await grants.locator('input[type="radio"][name$=".role"][value="CASE_READER"]').check();
  await grants.locator('input[type="radio"][name$=".scope.kind"][value="CASE"]').check();
  await grants.locator('[name$=".scope.target.caseReference"]').fill(reference);
};
const openActor = async (page: Page, actorId: string, name: string) => {
  await page.goto(`/people/${encodeURIComponent(actorId)}`, { waitUntil: "commit" });
  const expected = page.getByRole("dialog", { name, exact: true });
  const dialog = page.getByRole("dialog");
  const pressure = dialog
    .getByRole("alert")
    .filter({ hasText: "ClaimCore is busy. Try again in a moment." });
  for (let attempt = 0; attempt < 3; attempt += 1) {
    await expect
      .poll(async () => (await expected.isVisible()) || (await pressure.isVisible()))
      .toBe(true);
    if (await expected.isVisible()) {
      break;
    }
    await dialog.getByRole("button", { name: "Reload grants", exact: true }).click();
  }
  await expect(expected).toBeVisible();
};
const beginActorAct = async (
  page: Page,
  name: string,
  kind: AuthorityAct["kind"],
  language: Language,
) => {
  const p = createPresenter({ ...defaults, language });
  const person = page.getByRole("dialog", { name, exact: true });
  const button = person.getByRole("button", {
    name: p.text("ui.actDescription", {
      label: p.authorityLabel(kind),
      meaning: p.authorityMeaning(kind),
    }),
    exact: true,
  });
  await expect(button).toBeVisible();
  await expect(button).toHaveText(p.authorityLabel(kind));
  await button.click();
  await expect(
    page.getByRole("dialog", { name: p.authorityLabel(kind), exact: true }),
  ).toBeVisible();
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
      operationId: mintOperationId(),
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
test("browser admits service principals, reaffirms scoped grants and enablement, and renders core refusal", async ({
  page,
}) => {
  await openAuthenticated(page);
  const source = await syntheticPrincipals();
  const reference = `AUTHORITY-${randomUUID()}`;
  await openCase(page, reference);
  await goToView(page, "people");
  const subject = `browser-service-${randomUUID()}`;
  const name = `Authored browser service ${randomUUID()}`;
  // D2 admission records profile and the original case-only grant atomically; no extra installation access is added.
  await openAdmission(page, source.issuer, subject, name, reference);
  const admitted = await submitAuthority(page, "ACCEPTED");
  if (
    admitted === null ||
    admitted.subject.kind !== "AUTHORITY" ||
    admitted.subject.targetActor.kind !== "DISCLOSED"
  ) {
    throw new Error("Missing disclosed admitted service target.");
  }
  const { actorId } = admitted.subject.targetActor.actor;
  await openActor(page, actorId, name);
  await beginActorAct(page, name, "GRANT", "en");
  await chooseChoice(page, "act.role", "CASE_READER");
  await chooseChoice(page, "act.scope.kind", "CASE");
  await page.locator('[name="act.scope.target.caseReference"]').fill(reference);
  await submitAuthority(page, "ACCEPTED");
  await openActor(page, actorId, name);
  await beginActorAct(page, name, "DISABLE", "en");
  await submitAuthority(page, "ACCEPTED");
  await openActor(page, actorId, name);
  await beginActorAct(page, name, "GRANT", "en");
  const owner = page.locator('input[name="act.role"][value="OWNER"]');
  await expect(owner).toBeDisabled();
  await expect(owner).toHaveAccessibleDescription(/Not available for service accounts/u);
  await assertHumanOnlyBoundary(page, source.issuer, subject);
  // The active authority manager retains the existing missing-target diagnostic.
  // Unavailable callers remain non-disclosing at the service boundary.
  const missing = await browserRequest(page, "/api/operation.preview", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-ClaimCore-Antiforgery": await sessionToken(page),
    },
    body: JSON.stringify({
      kind: "AUTHORITY",
      operationId: mintOperationId(),
      target: { kind: "SERVICE", issuer: source.issuer, subject: `unregistered-${randomUUID()}` },
      act: { kind: "GRANT", role: "CASE_READER", scope: { kind: "INSTALLATION" } },
    }),
  });
  expect(missing.status).toBe(200);
  expect(missing.payload).toMatchObject({
    outcome: { kind: "REJECTED", data: { code: "ACTOR_UNKNOWN" } },
  });
  // Close the untouched grant and person drawers through their actual controls, then exercise a rendered admission refusal.
  await page
    .getByRole("dialog")
    .getByRole("button", { name: "Close", exact: true })
    .first()
    .click();
  await page
    .getByRole("dialog", { name, exact: true })
    .getByRole("button", { name: "Close", exact: true })
    .first()
    .click();
  await openAdmission(page, source.issuer, subject, name, reference);
  await submitAuthority(page, "REJECTED");
  await expect(page.getByRole("dialog").getByRole("alert")).toContainText(
    "This account is already registered.",
  );
  await expectAccessible(page);
});
