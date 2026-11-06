import { chooseChoice } from "./workspace-navigation";
import { freshLoginWorkflowTimeout } from "./admission-window";
import { registration } from "../tests/support/inputs";
import { seedPresentation, selectLanguage, ui } from "./localization-support";
import { casePath } from "../src/routing/routes";
import { isResponse } from "@contracts/client";
import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { openAuthenticated, authenticateSteward, expectAccessible } from "./session-helpers";
import { openCase, reviewDraft, startOpen, recordReviewed, startAct } from "./case-workflow";
import { fillBusinessField } from "./typed-entry";
import {
  recordWorkReview,
  workRequest,
  workConfirmButton,
  reviewReminderCompletion,
  selectReminderAction,
  selectCaseReminderState,
} from "./work-workflow";

let stewardCookies: Awaited<ReturnType<typeof authenticateSteward>>;
test.beforeAll(async ({ browser, baseURL }) => {
  test.setTimeout(freshLoginWorkflowTimeout);
  stewardCookies = await authenticateSteward(browser, baseURL);
});

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
  const { reminderId } = receipt.subject.effects.reminder;
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
  await startAct(page, "AMEND_REGISTRATION", "en");
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
  await expect(
    page
      .locator(".reminder-list li")
      .filter({ hasText: "Call claimant" })
      .locator(".reminder-timing"),
  ).toHaveText("Overdue");
  await expect(
    page
      .locator(".reminder-list li")
      .filter({ hasText: "Ask insurer" })
      .locator(".reminder-timing"),
  ).toHaveText("Upcoming");
  await reviewReminderCompletion(
    page,
    page.locator(".reminder-row").filter({ hasText: "Call claimant" }),
  );
  await recordWorkReview(page, "REMINDER");
  await selectReminderAction(
    page,
    page.locator(".reminder-row").filter({ hasText: "Ask insurer" }),
    "CANCEL",
  );
  await reviewDraft(page);
  await recordWorkReview(page, "REMINDER");
  await expect(page.locator(".case-reminders .work-attention")).toBeVisible();
  await selectCaseReminderState(page, "COMPLETED");
  await expect(page.locator(".reminder-list li")).toHaveCount(1);
  await expect(page.locator(".reminder-list li .reminder-timing")).toHaveCount(0);
  await selectReminderAction(page, page.locator(".reminder-row"), "REOPEN");
  await fillBusinessField(page, "reminderDueDate", "9999-12-30");
  await reviewDraft(page);
  await recordWorkReview(page, "REMINDER");
  await selectCaseReminderState(page, "PENDING");
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
  await selectReminderAction(page, page.locator(".reminder-row"), "COMPLETE_AND_PLAN_NEXT");
  await fillBusinessField(page, "reminderAction", "   ");
  await fillBusinessField(page, "reminderDueDate", "9999-12-31");
  const invalid = page.waitForResponse(
    (value) => new URL(value.url()).pathname === "/api/operation.preview",
  );
  await page
    .getByRole("dialog")
    .getByRole("button", { name: "Review changes", exact: true })
    .click();
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
  await selectReminderAction(page, page.locator(".reminder-row"), "COMPLETE_AND_PLAN_NEXT");
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
  await workConfirmButton(page).click();
  await page.getByRole("button", { name: "Send again", exact: true }).click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  expect(captured).toHaveLength(2);
  expect(captured[1]).toBe(captured[0]);
  const replacement = page.locator(".reminder-list li");
  await expect(replacement).toHaveCount(1);
  await expect(replacement).toContainText("Replacement after uncertainty");
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
      await page.locator("#case-summary-update").click();
      await page.locator('input[data-selects-fields~="handlingSummary"]').check();
      await fillBusinessField(page, "handlingSummary", text);
      await page
        .getByRole("dialog")
        .getByRole("button", { name: ui(language, "label.reviewTitle"), exact: true })
        .click();
      await expect(page.locator(".ui-dialog .observed-case-facts")).toBeVisible();
      await recordWorkReview(page, "WORK");
      await page
        .locator(".case-reminders")
        .getByRole("button", { name: ui(language, "ui.newReminder"), exact: true })
        .click();
      await fillBusinessField(page, "reminderAction", "😀".repeat(200));
      await fillBusinessField(page, "reminderDueDate", "9999-12-31");
      await page
        .getByRole("dialog")
        .getByRole("button", { name: ui(language, "label.reviewTitle"), exact: true })
        .click();
      await recordWorkReview(page, "REMINDER");
      await expect(page.locator("html")).toHaveAttribute("dir", language === "ar" ? "rtl" : "ltr");
      await expect(page.locator(".case-work .plain-text-value")).toHaveText(text);
      await expect(page.locator(".reminder-list li")).toHaveCount(1);
      expect(
        await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
      ).toBe(true);
      const heading = await page.locator(".case-heading").boundingBox();
      const title = await page.locator("#case-detail-title").boundingBox();
      expect(heading).not.toBeNull();
      expect(title).not.toBeNull();
      // A long primary action must wrap before it squeezes the primary case title into one-character lines.
      expect(title!.width).toBeGreaterThanOrEqual(Math.min(320, heading!.width * 0.75));
      await expectAccessible(page);
      await page.screenshot({
        path: test.info().outputPath(`work-${language}-${mode}.png`),
        // Bound this review artifact; full text, reflow and axe are checked above.
        fullPage: false,
        scale: "css",
        animations: "disabled",
      });
    });
  }
}

test("paid description correction preserves payment, then a closed case accepts summary and pending cleanup", async ({
  page,
}) => {
  await seedPresentation(page, "en", "en-GB");
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
  await startAct(page, "CORRECT", "en");
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
  await page.locator("#case-summary-update").click();
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
    page.locator(".case-reminders").getByRole("button", { name: "Add reminder", exact: true }),
  ).toHaveCount(0);
  const pending = page.locator(".reminder-list li");
  await expect(pending).toHaveCount(1);
  await expect(pending).toContainText(ui("en", "ui.caseWorkInactive"));
  await expect(pending.locator(".reminder-timing")).toHaveCount(0);
  await pending.getByRole("button", { name: "Available actions", exact: true }).click();
  const menu = page.getByRole("menu");
  await expect(menu.getByRole("menuitem", { name: "Cancel reminder", exact: true })).toBeVisible();
  await expect(menu.getByRole("menuitem", { name: "Update reminder", exact: true })).toHaveCount(0);
  await page.keyboard.press("Escape");
  await reviewReminderCompletion(page, pending);
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
  await page
    .getByRole("dialog")
    .getByRole("button", { name: "Review changes", exact: true })
    .click();
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
  await expect(page.getByRole("dialog").locator("[data-error-summary]")).toBeVisible();
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
  await selectReminderAction(page, page.locator(".reminder-row"), "COMPLETE_AND_PLAN_NEXT");
  await fillBusinessField(page, "reminderAction", "Planned replacement");
  await fillBusinessField(page, "reminderDueDate", "9999-12-31");
  const previewed = page.waitForResponse(
    (value) => new URL(value.url()).pathname === "/api/operation.preview",
  );
  const reviewed = await reviewDraft(page);
  const frozenBytes = (await previewed).request().postData();
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
  if (concurrent.outcome.kind !== "ACCEPTED") {
    throw new Error("Expected the independently recorded reminder update.");
  }
  const attempted = page.waitForResponse(
    (value) => new URL(value.url()).pathname === "/api/operation.execute",
  );
  await workConfirmButton(page).click();
  const response = await attempted;
  expect(response.request().postData()).toBe(frozenBytes);
  const frozen: unknown = response.request().postDataJSON();
  expect(frozen).toMatchObject({
    operationId: reviewed.operationId,
    kind: "REMINDER",
    act: { kind: "COMPLETE_AND_PLAN_NEXT", reminderId: initial.operationId, expectedRevision: 1 },
  });
  const rejected: unknown = await response.json();
  if (!isResponse("operation.execute", rejected)) {
    throw new Error("Expected conflict result");
  }
  expect(rejected.outcome).toMatchObject({
    kind: "REJECTED",
    data: { code: "REVISION_CONFLICT", actualRevision: 2 },
  });
  if (rejected.outcome.kind !== "REJECTED" || rejected.outcome.data.code !== "REVISION_CONFLICT") {
    throw new Error("Expected the authoritative reminder revision conflict.");
  }
  expect(rejected.outcome.data.changes.kind).toBe("PAGE");
  if (rejected.outcome.data.changes.kind !== "PAGE") {
    throw new Error("Expected the current owner's disclosed conflict changes.");
  }
  expect(rejected.outcome.data.changes.page.entries.map((entry) => entry.operationId)).toContain(
    concurrent.outcome.data.operationId,
  );
  const conflict = page.getByRole("dialog").locator(".revision-conflict");
  await expect(
    conflict.getByRole("heading", { name: "This case has changed", exact: true }),
  ).toBeVisible();
  await expect(conflict).toContainText("Your draft is kept.");
  await expect(conflict).toContainText("Changed by another session");
  await expect(
    conflict.getByRole("button", { name: "Review against latest", exact: true }),
  ).toBeEnabled();
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

test("published reminder rows show server timing in case and queue views across languages", async ({
  page,
  browser,
}) => {
  await openAuthenticated(page);
  const reference = `TIMING-${randomUUID()}`;
  await openCase(page, reference);
  const observed = await workRequest(page, "work.overview", {});
  if (observed.outcome.kind !== "SUCCEEDED") {
    throw new Error("Expected server date basis");
  }
  const { businessDate, timeZone } = observed.outcome.data.basis;
  const actions = [
    `${reference} past planned action`,
    `${reference} current planned action`,
    `${reference} future planned action`,
  ];
  for (const [index, dueDate] of ["0001-01-01", businessDate, "9999-12-31"].entries()) {
    const accepted = await workRequest(page, "operation.execute", {
      kind: "REMINDER",
      operationId: randomUUID(),
      target: { kind: "REFERENCE", caseReference: reference },
      act: { kind: "CREATE", action: actions[index]!, dueDate },
    });
    expect(accepted.outcome.kind).toBe("ACCEPTED");
  }
  await page.clock.setFixedTime(new Date("2031-06-15T12:00:00Z"));
  await page.reload();
  for (const language of ["en", "lv", "ar"] as const) {
    await selectLanguage(page, language);
    for (const [index, timing] of (["OVERDUE", "TODAY", "UPCOMING"] as const).entries()) {
      const item = page.locator(".reminder-list li").filter({ hasText: actions[index]! });
      await expect(item.locator(".reminder-timing")).toHaveText(
        ui(language, `reminderDue.${timing}.label`),
      );
      await expect(item).toContainText(ui(language, "reminderState.PENDING.label"));
      const details = item.locator(".reminder-record-details");
      await expect(details).not.toHaveAttribute("open");
      await details.locator("summary").click();
      await expect(details.locator(".observation-basis")).toContainText(timeZone);
      await details.locator("summary").click();
    }
  }
  await page.goto("/reminders");
  const reminderSearch = page.getByRole("search", { name: ui("ar", "ui.reminders"), exact: true });
  await reminderSearch.getByRole("searchbox").fill(reference);
  await reminderSearch.getByRole("button").click();
  await expect(page.locator(".reminder-list li")).toHaveCount(3);
  for (const [index, label] of ["متأخر", "اليوم", "قادم"].entries()) {
    const item = page.locator(".reminder-list li").filter({ hasText: actions[index]! });
    await expect(item.locator(".reminder-timing")).toHaveText(label);
    await expect(item.getByRole("link")).toHaveText(reference);
  }
  const stewardContext = await browser.newContext({
    baseURL: new URL(page.url()).origin,
    ignoreHTTPSErrors: true,
  });
  try {
    const steward = await stewardContext.newPage();
    await openAuthenticated(steward, "Cases", stewardCookies);
    const voided = await workRequest(steward, "operation.execute", {
      kind: "CASE",
      operationId: randomUUID(),
      target: { kind: "REFERENCE", caseReference: reference },
      expectedRevision: 1,
      act: { kind: "VOID", reason: "Synthetic timing test entry error" },
    });
    expect(voided.outcome.kind).toBe("ACCEPTED");
  } finally {
    await stewardContext.close();
  }
  await page.reload();
  await reminderSearch.getByRole("searchbox").fill(reference);
  await reminderSearch.getByRole("button").click();
  await expect(page.locator(".reminder-list li")).toHaveCount(0);
  const inactive = page.getByRole("checkbox", { name: ui("ar", "ui.includeInactiveWork") });
  await expect(inactive).toBeDisabled();
  await page.getByRole("button", { name: ui("ar", "ui.manualFilters"), exact: true }).click();
  await expect(page).toHaveURL(
    (url) => url.searchParams.get("filters") === "manual" && !url.searchParams.has("view"),
  );
  await inactive.check();
  await expect(page.locator(".reminder-list li")).toHaveCount(3);
  await expect(page.locator(".reminder-list li .reminder-timing")).toHaveCount(0);
  for (const item of await page.locator(".reminder-list li").all()) {
    await expect(item).toContainText(ui("ar", "ui.caseWorkInactive"));
    await expect(item).toContainText(ui("ar", "reminderState.PENDING.label"));
  }
});
