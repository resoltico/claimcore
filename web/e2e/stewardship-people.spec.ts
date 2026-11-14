import { observedAccess, acceptedSubject } from "./work-workflow";
import { syntheticPrincipals } from "./people-setup";
import type { ResponseFor, OperationTaskDefinition } from "@contracts/client";
import { expectCaseReadsSettled } from "./case-readiness";
import { operationId as mintOperationId } from "../src/operation/operationId";
import { freshLoginWorkflowTimeout } from "./admission-window";
import { type Language, defaults } from "../src/presentation/preferences";

import { createPresenter } from "../src/presentation/presenter";
import { goToView, showExactLookup, chooseChoice } from "./workspace-navigation";
import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "@playwright/test";
import { openCase, startAct, recordReviewed } from "./case-workflow";
import {
  expectAccessible,
  openAuthenticated,
  authenticateSteward,
  browserRequest,
  sessionToken,
} from "./session-helpers";
import { confirmReview } from "./localization-support";

import { recoveryRecords } from "./recovery-storage";
let stewardCookies: Awaited<ReturnType<typeof authenticateSteward>>;
test.beforeAll(async ({ browser, baseURL }) => {
  test.setTimeout(freshLoginWorkflowTimeout);
  stewardCookies = await authenticateSteward(browser, baseURL);
});
const stewardAct = async (
  page: Page,
  reference: string,
  kind: Extract<OperationTaskDefinition, { subject: "CASE" }>["act"],
  reason?: string,
) => {
  const p = createPresenter({
    ...defaults,
    language: "en",
  });
  const label = p.actLabel(kind);
  await startAct(page, kind, "en");
  const dialog = page.getByRole("dialog", {
    name: label,
    exact: true,
  });
  if (reason !== undefined) {
    await dialog
      .getByLabel("Reason", {
        exact: true,
      })
      .fill(reason);
  }
  if (kind === "case.erase") {
    await expect(dialog).toContainText("Copies held elsewhere are not affected.");
  }
  const previewed = page.waitForResponse(
    (reply) => new URL(reply.url()).pathname === "/api/operation.preview",
  );
  await dialog
    .getByRole("button", {
      name: "Review changes",
      exact: true,
    })
    .click();
  const preview = await previewed;
  await confirmReview(page);
  const action = dialog.locator("button[data-confirm-operation]");
  if (kind === "case.erase") {
    await expect(dialog).toContainText(
      "This permanently removes the case details, work and reminders from ClaimCore.",
    );
    const challenge = dialog.getByRole("textbox", {
      name: "Case reference (exact)",
      exact: true,
    });
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
  const operationExecuteReply = (await reply.json()) as ResponseFor<"operation.execute", "HUMAN">;
  if (operationExecuteReply.representation !== "HUMAN") {
    throw new Error("Missing stewardship response.");
  }
  expect(operationExecuteReply.outcome.kind).toBe("ACCEPTED");
  if (operationExecuteReply.outcome.kind === "ACCEPTED") {
    expect(
      (
        await acceptedSubject(
          page,
          operationExecuteReply.operationId,
          observedAccess(reply.request()),
        )
      ).kind,
    ).toBe("CASE");
    const references = operationExecuteReply.outcome.document.screen.regions.flatMap((region) =>
      region.role === "IDENTITY" && region.content.kind === "ENTRIES"
        ? region.content.entries.map((entry) => entry.value)
        : [],
    );
    expect(references).toContainEqual({
      kind: "REFERENCE",
      value: reference,
    });
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
    await steward
      .getByRole("button", {
        name: "Open by exact reference",
        exact: true,
      })
      .click();
    await stewardAct(steward, reference, "case.void", "Synthetic entry error");
    await expect(
      steward.locator(
        '.case-header-state .code-value[data-vocabulary="lifecycle"][data-code="VOIDED"]',
      ),
    ).toContainText("Voided");
    await stewardAct(steward, reference, "case.reinstate", "Synthetic correction");
    await stewardAct(steward, reference, "case.erase");
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
const peopleTarget = async (page: Page, issuer: string, subject: string) => {
  await chooseChoice(page, "input:principal.kind", "SERVICE");
  await expect(page.locator('[name="input:principal.issuer"]')).toHaveValue(issuer);
  await expect(page.locator('[name="input:principal.issuer"]')).toBeEnabled();
  await page.locator('[name="input:principal.subject"]').fill(subject);
};
const submitPeople = async (page: Page, expected: "ACCEPTED" | "REJECTED") => {
  const dialog = page.getByRole("dialog");
  const previewed = page.waitForResponse(
    (reply) => new URL(reply.url()).pathname === "/api/operation.preview",
  );
  await dialog
    .getByRole("button", {
      name: "Review changes",
      exact: true,
    })
    .click();
  const previewResponse = await previewed;
  const operationPreviewReply = (await previewResponse.json()) as ResponseFor<
    "operation.preview",
    "HUMAN"
  >;
  if (operationPreviewReply.representation !== "HUMAN") {
    throw new Error("Missing people preview.");
  }
  expect(operationPreviewReply.outcome.kind).toBe(
    expected === "ACCEPTED" ? "REVIEWED" : "REJECTED",
  );
  if (expected === "REJECTED") {
    await expect(dialog).toBeVisible();
    await expect(dialog.getByRole("alert")).toBeVisible();
    return null;
  }
  if (
    operationPreviewReply.outcome.kind !== "REVIEWED" ||
    operationPreviewReply.outcome.review.document.actions[0]?.binding.kind !== "PEOPLE"
  ) {
    throw new Error("Expected people review.");
  }
  await confirmReview(page);
  const pending = page.waitForResponse(
    (reply) => new URL(reply.url()).pathname === "/api/operation.execute",
  );
  await dialog.locator("button[data-confirm-operation]").click();
  const reply = await pending;
  expect(reply.request().postData()).toBe(previewResponse.request().postData());
  const operationExecuteReply = (await reply.json()) as ResponseFor<"operation.execute", "HUMAN">;
  if (
    operationExecuteReply.representation !== "HUMAN" ||
    operationExecuteReply.outcome.kind !== "ACCEPTED" ||
    (
      await acceptedSubject(
        page,
        operationExecuteReply.operationId,
        observedAccess(reply.request()),
      )
    ).kind !== "PEOPLE"
  ) {
    throw new Error("Missing accepted people response.");
  }
  const receipt = page.locator("section.receipt");
  await expect(receipt.locator('[data-entry="operationId"]')).toContainText(
    operationExecuteReply.operationId,
  );
  const technical = receipt.locator('details[data-region="DETAILS"]');
  await technical.locator(":scope > summary").click();
  await expect(technical.locator('[data-entry="operationId"] .identifier-value')).toHaveText(
    operationExecuteReply.operationId,
  );
  await expect.poll(() => recoveryRecords(page)).toEqual([]);
  await expect(page.getByRole("dialog")).toHaveCount(0);
  return operationExecuteReply.outcome.document;
};
const openAdmission = async (
  page: Page,
  issuer: string,
  subject: string,
  name: string,
  reference: string,
) => {
  await page
    .getByRole("button", {
      name: "Add person",
      exact: true,
    })
    .first()
    .click();
  const dialog = page.getByRole("dialog", {
    name: "Add person",
    exact: true,
  });
  await peopleTarget(page, issuer, subject);
  await dialog.locator('[name="input:profile.displayName"]').fill(name);
  const grants = dialog.locator('fieldset[id="field-peopleGrants"]');
  await grants
    .getByRole("radio", {
      name: "Case reader",
      exact: true,
    })
    .check();
  await grants
    .getByRole("radio", {
      name: "One case",
      exact: true,
    })
    .check();
  await grants
    .getByRole("textbox", {
      name: "Case reference",
      exact: true,
    })
    .fill(reference);
};
const openActor = async (page: Page, actorId: string, name: string) => {
  await page.goto(`/people/${encodeURIComponent(actorId)}`, {
    waitUntil: "commit",
  });
  const expected = page.getByRole("dialog", {
    name,
    exact: true,
  });
  const dialog = page.getByRole("dialog");
  const pressure = dialog.getByRole("alert").filter({
    hasText: "ClaimCore is busy. Try again in a moment.",
  });
  for (let attempt = 0; attempt < 3; attempt += 1) {
    await expect
      .poll(async () => (await expected.isVisible()) || (await pressure.isVisible()))
      .toBe(true);
    if (await expected.isVisible()) {
      break;
    }
    await dialog
      .getByRole("button", {
        name: "Reload grants",
        exact: true,
      })
      .click();
  }
  await expect(expected).toBeVisible();
};
const beginActorAct = async (
  page: Page,
  name: string,
  kind: Extract<OperationTaskDefinition, { subject: "PEOPLE" }>["act"],
  language: Language,
) => {
  const p = createPresenter({
    ...defaults,
    language,
  });
  const person = page.getByRole("dialog", {
    name,
    exact: true,
  });
  const button = person.getByRole("button", {
    name: p.text("ui.actDescription", {
      label: p.peopleLabel(kind),
      meaning: p.peopleMeaning(kind),
    }),
    exact: true,
  });
  await expect(button).toBeVisible();
  await expect(button).toHaveText(p.peopleLabel(kind));
  await button.click();
  await expect(
    page.getByRole("dialog", {
      name: p.peopleLabel(kind),
      exact: true,
    }),
  ).toBeVisible();
};
const assertHumanOnlyBoundary = async (page: Page, actorId: string) => {
  const response = await browserRequest(page, "/api/operation.preview", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-ClaimCore-Antiforgery": await sessionToken(page),
    },
    body: JSON.stringify({
      operationId: mintOperationId(),
      task: "people.grant",
      binding: {
        kind: "PEOPLE",
        actorId,
      },
      inputs: { "input:role": "OWNER", "input:scope": "INSTALLATION" },
    }),
  });
  const value = response.payload;
  expect(value).toMatchObject({
    outcome: {
      kind: "REJECTED",
      rejection: {
        code: "HUMAN_PRINCIPAL_REQUIRED",
      },
    },
  });
};
test("browser admits service principals, reaffirms scoped grants and enablement, and renders core refusal", async ({
  page,
  browserName,
}, testInfo) => {
  await openAuthenticated(page);
  const source = await syntheticPrincipals();
  const reference = `PEOPLE-${randomUUID()}`;
  await openCase(page, reference);
  await goToView(page, "people");
  const subject = `browser-service-${randomUUID()}`;
  const name = `Authored browser service ${randomUUID()}`;
  // D2 admission records profile and the original case-only grant atomically; no extra installation access is added.
  await openAdmission(page, source.issuer, subject, name, reference);
  const admitted = await submitPeople(page, "ACCEPTED");
  if (admitted === null || admitted.disclosure !== "CURRENT") {
    throw new Error("Missing disclosed admitted service target.");
  }
  const receiptDetails = page.locator('section.receipt details[data-region="DETAILS"]');
  await receiptDetails.locator(":scope > summary").click();
  await expect(receiptDetails).not.toHaveAttribute("open");
  if (browserName === "chromium") {
    await page.locator("section.receipt").scrollIntoViewIfNeeded();
    await page.locator("section.receipt").screenshot({
      path: testInfo.outputPath("people-receipt-en-light.png"),
    });
  }
  const accepted = await acceptedSubject(page, admitted.subject.operationId);
  if (accepted.kind !== "PEOPLE") {
    throw new Error("People receipt required");
  }
  const actorId =
    accepted.targetActor.kind === "DISCLOSED"
      ? accepted.targetActor.actor.actorId
      : accepted.targetActor.actorId;
  await openActor(page, actorId, name);
  await beginActorAct(page, name, "people.grant", "en");
  await chooseChoice(page, "input:role", "CASE_READER");
  await chooseChoice(page, "input:scope", "CASE");
  await page.locator('[name="field:caseReference"]').fill(reference);
  await submitPeople(page, "ACCEPTED");
  await openActor(page, actorId, name);
  await beginActorAct(page, name, "people.disable", "en");
  await submitPeople(page, "ACCEPTED");
  await openActor(page, actorId, name);
  await beginActorAct(page, name, "people.grant", "en");
  const owner = page.locator('input[name="input:role"][value="OWNER"]');
  await expect(owner).toHaveCount(0);
  await assertHumanOnlyBoundary(page, actorId);
  // ActorId resolution keeps absent and inaccessible subjects non-disclosing.
  const missing = await browserRequest(page, "/api/operation.preview", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-ClaimCore-Antiforgery": await sessionToken(page),
    },
    body: JSON.stringify({
      operationId: mintOperationId(),
      task: "people.grant",
      binding: {
        kind: "PEOPLE",
        actorId: randomUUID(),
      },
      inputs: { "input:role": "CASE_READER", "input:scope": "INSTALLATION" },
    }),
  });
  expect(missing.status).toBe(200);
  expect(missing.payload).toMatchObject({
    outcome: {
      kind: "REJECTED",
      rejection: {
        code: "RESOURCE_UNAVAILABLE",
      },
    },
  });
  // Close the untouched grant and person drawers through their actual controls, then exercise a rendered admission refusal.
  await page
    .getByRole("dialog")
    .getByRole("button", {
      name: "Close",
      exact: true,
    })
    .first()
    .click();
  await page
    .getByRole("dialog", {
      name,
      exact: true,
    })
    .getByRole("button", {
      name: "Close",
      exact: true,
    })
    .first()
    .click();
  await openAdmission(page, source.issuer, subject, name, reference);
  await submitPeople(page, "REJECTED");
  await expect(page.getByRole("dialog").getByRole("alert")).toContainText(
    "This account is already registered.",
  );
  await expectAccessible(page);
});
