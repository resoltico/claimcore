import { isResponse } from "@contracts/client";
import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { openAuthenticated, expectAccessible } from "./session-helpers";
import { openCase, reviewDraft, startOpen, recordReviewed } from "./case-workflow";
import { fillBusinessField } from "./typed-entry";
import { recordWorkReview, workRequest } from "./work-workflow";

const plan = async (page: Parameters<typeof openCase>[0], action: string, due: string) => {
  await page.getByRole("button", { name: "Add reminder", exact: true }).click();
  await fillBusinessField(page, "reminderAction", action);
  await fillBusinessField(page, "reminderDueDate", due);
  await reviewDraft(page);
  return recordWorkReview(page, "REMINDER");
};

test("stores exact optional LF incident text and clears it without a financial assertion", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `NARRATIVE-${randomUUID()}`;
  await startOpen(page, reference);
  await page.locator('input[data-selects-fields~="incidentDescription"]').check();
  const description = `حادث في الطريق\n\n${"😀".repeat(582)}`;
  await fillBusinessField(page, "incidentDescription", description);
  await expect(page.locator('textarea[data-field-name="incidentDescription"]')).not.toHaveAttribute(
    "maxlength",
  );
  await reviewDraft(page);
  await recordReviewed(page);
  const recorded = await workRequest(page, "case.get", {
    target: { kind: "REFERENCE", caseReference: reference },
  });
  if (recorded.outcome.kind !== "SUCCEEDED" || recorded.outcome.data.state.kind === "ERASED") {
    throw new Error("Expected live case");
  }
  expect(recorded.outcome.data.state.fields.registration.incidentDescription).toBe(description);
  await expect(page.locator('[data-field-name="incidentDescription"].field-row')).toContainText(
    "حادث في الطريق",
  );
  await page.getByRole("button", { name: /^Amend registration:/u }).click();
  await page.locator('input[data-selects-fields~="incidentDescription"]').check();
  await page.getByRole("checkbox", { name: "Clear recorded value", exact: true }).check();
  await reviewDraft(page);
  await recordReviewed(page);
  const cleared = await workRequest(page, "case.get", {
    target: { kind: "REFERENCE", caseReference: reference },
  });
  if (cleared.outcome.kind !== "SUCCEEDED" || cleared.outcome.data.state.kind === "ERASED") {
    throw new Error("Expected live case");
  }
  expect(cleared.outcome.data.state.fields.registration.incidentDescription).toBeNull();
  expect(cleared.outcome.data.state.fields.decision).toBeNull();
  expect(cleared.outcome.data.state.fields.payment).toBeNull();
});

test("supports multiple independently revised reminders, terminal records, reopen and last-completion attention", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `REMINDERS-${randomUUID()}`;
  await openCase(page, reference);
  const first = await plan(page, "Call claimant", "0001-01-01");
  const second = await plan(page, "Ask insurer", "9999-12-31");
  expect(first.operationId).not.toBe(second.operationId);
  await expect(page.locator(".reminder-list li")).toHaveCount(2);
  await page
    .locator(".reminder-list li")
    .filter({ hasText: "Call claimant" })
    .getByRole("button", { name: "Complete reminder", exact: true })
    .click();
  await reviewDraft(page);
  await recordWorkReview(page, "REMINDER");
  await page
    .locator(".reminder-list li")
    .filter({ hasText: "Ask insurer" })
    .getByRole("button", { name: "Cancel reminder", exact: true })
    .click();
  await reviewDraft(page);
  await recordWorkReview(page, "REMINDER");
  await expect(page.locator(".case-work .work-attention")).toBeVisible();
  await page.locator('[aria-labelledby="case-reminders-title"] select').selectOption("COMPLETED");
  await expect(page.locator(".reminder-list li")).toHaveCount(1);
  await page
    .locator(".reminder-list li")
    .getByRole("button", { name: "Reopen reminder", exact: true })
    .click();
  await fillBusinessField(page, "reminderDueDate", "9999-12-30");
  await reviewDraft(page);
  await recordWorkReview(page, "REMINDER");
  await page.locator('[aria-labelledby="case-reminders-title"] select').selectOption("PENDING");
  await expect(page.locator(".reminder-list li")).toHaveCount(1);
  const current = await workRequest(page, "case.get", {
    target: { kind: "REFERENCE", caseReference: reference },
  });
  if (current.outcome.kind !== "SUCCEEDED") {
    throw new Error("Expected case");
  }
  expect(current.outcome.data.revision).toBe(1);
  expect(current.outcome.data.work.revision).toBe(0);
  expect(current.outcome.data.work.pendingReminders).toBe(1);
  expect(current.outcome.data.work.needsNextAction).toBe(false);
  await expectAccessible(page);
});

test("a compound action has one correlated accepting operation and a new reminder with that identity", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `COMPOUND-${randomUUID()}`;
  await openCase(page, reference);
  await plan(page, "First action", "2026-01-01");
  await page
    .locator(".reminder-list li")
    .getByRole("button", { name: "Complete and plan next action", exact: true })
    .click();
  await fillBusinessField(page, "reminderAction", "Second action");
  await fillBusinessField(page, "reminderDueDate", "9999-12-31");
  const reviewed = await reviewDraft(page);
  const receipt = await recordWorkReview(page, "REMINDER");
  expect(receipt.operationId).toBe(reviewed.operationId);
  if (
    receipt.subject.kind !== "REMINDER" ||
    receipt.subject.effects.kind !== "COMPLETED_AND_PLANNED"
  ) {
    throw new Error("Expected bounded pair");
  }
  expect(receipt.subject.effects.next.reminderId).toBe(receipt.operationId);
  expect(receipt.subject.effects.next.revision).toBe(1);
  expect(receipt.subject.effects.completed.revision).toBe(2);
  await expect(page.locator(".reminder-list li")).toHaveCount(1);
  await expect(page.locator(".reminder-list li")).toContainText("Second action");
});

test("a lost compound acknowledgement retries the identical pair without another reminder", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `LOST-COMPOUND-${randomUUID()}`;
  await openCase(page, reference);
  await plan(page, "Current action", "2026-01-01");
  await page
    .locator(".reminder-list li")
    .getByRole("button", { name: "Complete and plan next action", exact: true })
    .click();
  await fillBusinessField(page, "reminderAction", "Replacement after uncertainty");
  await fillBusinessField(page, "reminderDueDate", "9999-12-31");
  const reviewed = await reviewDraft(page);
  const captured: string[] = [];
  let interrupted = false;
  await page.route("**/api/operation.execute", async (route) => {
    const bytes = route.request().postData()!;
    captured.push(bytes);
    if (interrupted) {
      await route.continue();
    } else {
      interrupted = true;
      const accepted = await route.fetch();
      const payload: unknown = await accepted.json();
      if (!isResponse("operation.execute", payload) || payload.outcome.kind !== "ACCEPTED") {
        throw new Error("Expected accepted compound before acknowledgement loss");
      }
      await route.abort("connectionreset");
    }
  });
  await page
    .getByRole("checkbox", {
      name: "I confirm this reminder operation and its stated effects.",
      exact: true,
    })
    .check();
  await page.getByRole("button", { name: "Record changes", exact: true }).click();
  await page.getByRole("button", { name: "Send again", exact: true }).click();
  await expect(page.locator(".work-operation")).toHaveCount(0);
  expect(captured).toHaveLength(2);
  expect(captured[1]).toBe(captured[0]);
  const reminders = await workRequest(page, "reminder.list", {
    target: { kind: "REFERENCE", caseReference: reference },
    includeInactive: true,
    state: "PENDING",
    limit: 50,
  });
  if (reminders.outcome.kind !== "SUCCEEDED") {
    throw new Error("Expected pending reminders");
  }
  expect(reminders.outcome.data.items).toHaveLength(1);
  expect(reminders.outcome.data.items[0]!.reminder.reminderId).toBe(reviewed.operationId);
});
