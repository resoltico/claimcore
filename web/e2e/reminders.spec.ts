import { chooseChoice } from "./workspace-navigation";
import { registration } from "../tests/support/inputs";
import { seedPresentation, ui } from "./localization-support";
import { casePath } from "../src/routing/routes";
import { isResponse } from "@contracts/client";
import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { openAuthenticated, expectAccessible } from "./session-helpers";
import { openCase, reviewDraft, startOpen, recordReviewed, startAct } from "./case-workflow";
import { fillBusinessField } from "./typed-entry";
import { recordWorkReview, workRequest } from "./work-workflow";

const plan = async (page: Parameters<typeof openCase>[0], action: string, due: string) => {
  await page.getByRole("button", { name: "Add reminder", exact: true }).click();
  await fillBusinessField(page, "reminderAction", action);
  await fillBusinessField(page, "reminderDueDate", due);
  await reviewDraft(page);
  const receipt = await recordWorkReview(page, "REMINDER");
  // The raw fixture is a separate client; observe the accepted current page before its next request.
  if (
    receipt.subject.kind !== "REMINDER" ||
    receipt.subject.act !== "CREATE" ||
    receipt.subject.effects.kind !== "SINGLE"
  ) {
    throw new Error("Expected the accepted reminder creation identity.");
  }
  const reminderId = receipt.subject.effects.reminder.reminderId;
  await expect(page.locator(`[id="reminder-${reminderId}-COMPLETE"]`)).toBeVisible();
  return receipt;
};

test("stores exact optional LF incident text and clears it without a financial assertion", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `NARRATIVE-${randomUUID()}`;
  await startOpen(page, reference);
  await page.locator('input[data-selects-fields~="incidentDescription"]').check();
  const description = `حادث في الطريق\n\n${"😀".repeat(584)}`;
  expect(Array.from(description)).toHaveLength(600);
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
  await page.screenshot({
    path: test.info().outputPath("case-incident-600-lf.png"),
    fullPage: true,
    animations: "disabled",
  });
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
  await fillBusinessField(page, "reminderAction", "   ");
  await fillBusinessField(page, "reminderDueDate", "9999-12-31");
  const invalid = page.waitForResponse(
    (value) => new URL(value.url()).pathname === "/api/operation.preview",
  );
  await page.locator('.work-operation form button[type="submit"]').click();
  const refused: unknown = await (await invalid).json();
  if (!isResponse("operation.preview", refused)) {
    throw new Error("Expected compound validation result");
  }
  expect(refused.outcome).toMatchObject({
    kind: "REJECTED",
    data: { code: "INVALID_INPUT", issues: [{ field: "reminderAction", code: "TEXT_REQUIRED" }] },
  });
  const unchanged = await workRequest(page, "reminder.list", {
    target: { kind: "REFERENCE", caseReference: reference },
    state: "PENDING",
    limit: 50,
  });
  if (unchanged.outcome.kind !== "SUCCEEDED") {
    throw new Error("Expected unchanged pending action");
  }
  expect(unchanged.outcome.data.items).toHaveLength(1);
  expect(unchanged.outcome.data.items[0]!.reminder).toMatchObject({
    revision: 1,
    state: "PENDING",
    action: "First action",
  });
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

for (const language of ["en", "lv", "ar"] as const) {
  for (const mode of ["wide-light", "narrow-dark", "zoom-dark"] as const) {
    test(`connected maximum work text ${language} ${mode}`, async ({ page }) => {
      const locale = ({ en: "en-GB", lv: "lv-LV", ar: "ar-EG" } as const)[language];
      await page.setViewportSize({
        width: { "wide-light": 1440, "zoom-dark": 640, "narrow-dark": 390 }[mode],
        height: 900,
      });
      await page.emulateMedia({
        colorScheme: mode === "wide-light" ? "light" : "dark",
        reducedMotion: "reduce",
      });
      await seedPresentation(page, language, locale);
      await openAuthenticated(page, ui(language, "ui.cases"));
      const caseId = randomUUID();
      const reference = `LOCAL-WORK-${randomUUID()}`;
      const prefix = "الملخص\n\n";
      const text = `${prefix}${"😀".repeat(600 - Array.from(prefix).length)}`;
      const opened = await workRequest(page, "operation.execute", {
        kind: "CASE",
        operationId: caseId,
        target: { kind: "REFERENCE", caseReference: reference },
        expectedRevision: 0,
        act: { kind: "OPEN", values: registration({ incidentDescription: text }) },
      });
      expect(opened.outcome.kind).toBe("ACCEPTED");
      await page.goto(casePath(caseId));
      if (mode === "zoom-dark") {
        await page.evaluate(() => {
          document.documentElement.style.zoom = "2";
        });
      }
      await page.locator(".case-work .section-heading button").click();
      await page.locator('input[data-selects-fields~="handlingSummary"]').check();
      await fillBusinessField(page, "handlingSummary", text);
      await page.locator('.work-operation form button[type="submit"]').click();
      await expect(page.locator(".review-modal .observed-case-facts")).toBeVisible();
      await page.locator('.review-modal input[type="checkbox"]').check();
      const acceptedWork = page.waitForResponse(
        (value) => new URL(value.url()).pathname === "/api/operation.execute",
      );
      await page.locator(".review-modal button.primary").click();
      const workPayload: unknown = await (await acceptedWork).json();
      expect(isResponse("operation.execute", workPayload)).toBe(true);
      await expect(page.locator(".work-operation")).toHaveCount(0);
      await page
        .locator(".case-work")
        .getByRole("button", { name: ui(language, "ui.newReminder"), exact: true })
        .click();
      await fillBusinessField(page, "reminderAction", "😀".repeat(200));
      await fillBusinessField(page, "reminderDueDate", "9999-12-31");
      await page.locator('.work-operation form button[type="submit"]').click();
      await page.locator('.review-modal input[type="checkbox"]').check();
      const acceptedReminder = page.waitForResponse(
        (value) => new URL(value.url()).pathname === "/api/operation.execute",
      );
      await page.locator(".review-modal button.primary").click();
      await acceptedReminder;
      await expect(page.locator(".work-operation")).toHaveCount(0);
      await expect(page.locator("html")).toHaveAttribute("dir", language === "ar" ? "rtl" : "ltr");
      await expect(page.locator(".case-work .plain-text-value")).toHaveText(text);
      await expect(page.locator(".reminder-list li")).toHaveCount(1);
      expect(
        await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
      ).toBe(true);
      await expectAccessible(page);
      await page.screenshot({
        path: test.info().outputPath(`work-${language}-${mode}.png`),
        fullPage: true,
        animations: "disabled",
      });
    });
  }
}

test("paid description correction preserves payment, then a closed case accepts summary and pending cleanup", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `PAID-WORK-${randomUUID()}`;
  await openCase(page, reference);
  await plan(page, "Finish existing action", "2026-01-01");
  for (const [revision, act] of [
    [
      1,
      {
        kind: "DECIDE",
        values: { paymentDecisionDate: "2026-09-03", payableAmount: "500", payableCurrency: "EUR" },
      },
    ],
    [2, { kind: "RECORD_PAYMENT", values: { paymentDate: "2026-09-04" } }],
  ] as const) {
    const seeded = await workRequest(page, "operation.execute", {
      kind: "CASE",
      operationId: randomUUID(),
      target: { kind: "REFERENCE", caseReference: reference },
      expectedRevision: revision,
      act,
    });
    expect(seeded.outcome.kind).toBe("ACCEPTED");
  }
  await page.reload();
  await startAct(page, "Correct case facts");
  await chooseChoice(page, "act.groups.registration.kind", "UPDATE");
  await page.locator('input[data-selects-fields~="incidentDescription"]').check();
  const description = "Paid case description\nNo new payment assertion.";
  await fillBusinessField(page, "incidentDescription", description);
  const advisory = page.waitForResponse(
    (value) => new URL(value.url()).pathname === "/api/operation.preview",
  );
  await reviewDraft(page);
  const requested: unknown = (await advisory).request().postDataJSON();
  expect(requested).toMatchObject({
    act: {
      kind: "CORRECT",
      groups: {
        registration: { kind: "UPDATE", values: { incidentDescription: description } },
        decision: { kind: "KEEP" },
        payment: { kind: "KEEP" },
      },
    },
  });
  await recordReviewed(page);
  const closed = await workRequest(page, "operation.execute", {
    kind: "CASE",
    operationId: randomUUID(),
    target: { kind: "REFERENCE", caseReference: reference },
    expectedRevision: 4,
    act: { kind: "CLOSE" },
  });
  expect(closed.outcome.kind).toBe("ACCEPTED");
  await page.reload();
  await page
    .locator(".case-work")
    .getByRole("button", { name: "Update case work", exact: true })
    .click();
  await expect(page.locator('[name="act.values.responsibleActor.kind"]')).toHaveCount(0);
  await page.locator('input[data-selects-fields~="handlingSummary"]').check();
  await fillBusinessField(
    page,
    "handlingSummary",
    "Closed; awaiting confirmation.\nKeep the accepted payment facts.",
  );
  await reviewDraft(page);
  await expect(
    page.locator('.observed-case-facts [data-field-name="payableAmount"]'),
  ).toContainText("500.00");
  await expect(page.locator('.observed-case-facts [data-field-name="paymentDate"]')).toContainText(
    "04/09/2026",
  );
  await recordWorkReview(page, "WORK");
  await expect(
    page.locator(".case-work").getByRole("button", { name: "Add reminder", exact: true }),
  ).toHaveCount(0);
  const pending = page.locator(".reminder-list li");
  await expect(pending).toHaveCount(1);
  await expect(pending.getByRole("button", { name: "Update reminder", exact: true })).toHaveCount(
    0,
  );
  await pending.getByRole("button", { name: "Complete reminder", exact: true }).click();
  await reviewDraft(page);
  await recordWorkReview(page, "REMINDER");
  const current = await workRequest(page, "case.get", {
    target: { kind: "REFERENCE", caseReference: reference },
  });
  if (current.outcome.kind !== "SUCCEEDED" || current.outcome.data.state.kind === "ERASED") {
    throw new Error("Expected closed paid case");
  }
  expect(current.outcome.data.revision).toBe(5);
  expect(current.outcome.data.state.fields.registration.incidentDescription).toBe(description);
  expect(current.outcome.data.state.fields.decision).toEqual({
    paymentDecisionDate: "2026-09-03",
    payableAmount: "500",
    payableCurrency: "EUR",
  });
  expect(current.outcome.data.state.fields.payment).toEqual({ paymentDate: "2026-09-04" });
  expect(current.outcome.data.work).toMatchObject({
    revision: 1,
    pendingReminders: 0,
    needsNextAction: false,
    due: "INACTIVE",
  });
});

test("a single-line action retains raw linebreaks for core refusal instead of silently rewriting pasted text", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `ACTION-LF-${randomUUID()}`;
  await openCase(page, reference);
  await page.getByRole("button", { name: "Add reminder", exact: true }).click();
  const action = page.locator('textarea[data-field-name="reminderAction"]');
  await action.fill("first\nsecond");
  await expect(action).toHaveValue("first\nsecond");
  await fillBusinessField(page, "reminderDueDate", "9999-12-31");
  const advisory = page.waitForResponse(
    (value) => new URL(value.url()).pathname === "/api/operation.preview",
  );
  await page.locator('.work-operation form button[type="submit"]').click();
  const reply = await advisory;
  const input: unknown = reply.request().postDataJSON();
  expect(input).toMatchObject({
    kind: "REMINDER",
    act: { kind: "CREATE", action: "first\nsecond" },
  });
  const payload: unknown = await reply.json();
  if (!isResponse("operation.preview", payload)) {
    throw new Error("Expected protocol refusal");
  }
  expect(payload.outcome).toMatchObject({
    kind: "REJECTED",
    data: {
      code: "INVALID_INPUT",
      issues: [{ field: "reminderAction", code: "CONTROL_CHARACTERS" }],
    },
  });
  await expect(page.locator(".work-operation [data-error-summary]")).toBeVisible();
  const reminders = await workRequest(page, "reminder.list", {
    target: { kind: "REFERENCE", caseReference: reference },
    limit: 50,
  });
  if (reminders.outcome.kind !== "SUCCEEDED") {
    throw new Error("Expected current reminders");
  }
  expect(reminders.outcome.data.items).toHaveLength(0);
});

test("a reminder revision change after compound preview refuses both effects without rebasing", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `STALE-COMPOUND-${randomUUID()}`;
  await openCase(page, reference);
  const initial = await plan(page, "Original action", "2026-01-01");
  await page
    .locator(".reminder-list li")
    .getByRole("button", { name: "Complete and plan next action", exact: true })
    .click();
  await fillBusinessField(page, "reminderAction", "Planned replacement");
  await fillBusinessField(page, "reminderDueDate", "9999-12-31");
  const reviewed = await reviewDraft(page);
  const concurrent = await workRequest(page, "operation.execute", {
    kind: "REMINDER",
    operationId: randomUUID(),
    target: { kind: "REFERENCE", caseReference: reference },
    act: {
      kind: "UPDATE",
      reminderId: initial.operationId,
      expectedRevision: 1,
      action: "Changed by another session",
      dueDate: "2026-11-10",
    },
  });
  expect(concurrent.outcome.kind).toBe("ACCEPTED");
  await page
    .getByRole("checkbox", {
      name: "I confirm this reminder operation and its stated effects.",
      exact: true,
    })
    .check();
  const attempted = page.waitForResponse(
    (value) => new URL(value.url()).pathname === "/api/operation.execute",
  );
  await page.getByRole("button", { name: "Record changes", exact: true }).click();
  const rejected: unknown = await (await attempted).json();
  if (!isResponse("operation.execute", rejected)) {
    throw new Error("Expected conflict result");
  }
  expect(rejected.outcome).toMatchObject({ kind: "REJECTED", data: { code: "REVISION_CONFLICT" } });
  await expect(page.locator(".work-operation [data-error-summary]")).toBeVisible();
  const current = await workRequest(page, "reminder.list", {
    target: { kind: "REFERENCE", caseReference: reference },
    state: "PENDING",
    limit: 50,
  });
  if (current.outcome.kind !== "SUCCEEDED") {
    throw new Error("Expected pending action");
  }
  expect(current.outcome.data.items).toHaveLength(1);
  expect(current.outcome.data.items[0]!.reminder).toMatchObject({
    reminderId: initial.operationId,
    revision: 2,
    state: "PENDING",
    action: "Changed by another session",
  });
  const replacement = await workRequest(page, "reminder.get", {
    target: { kind: "REFERENCE", caseReference: reference },
    reminderId: reviewed.operationId,
  });
  expect(replacement.outcome.kind).toBe("REJECTED");
});
