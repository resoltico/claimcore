import { feedbackFailures } from "./design-matrix-checks";
import { noHorizontalOverflow } from "./layout-support";
import {
  acceptedSubject,
  recordWorkReview,
  workRequest,
  workConfirmButton,
  reminderRows,
  reviewReminderCompletion,
  selectReminderAction,
  selectCaseReminderState,
} from "./work-workflow";
import { prefixedInputs } from "../tests/support/submission-inputs";
import type { ResponseFor } from "@contracts/client";
import { expectCaseReadsSettled } from "./case-readiness";
import { operationId as mintOperationId } from "../src/operation/operationId";
import { chooseChoice } from "./workspace-navigation";
import { freshLoginWorkflowTimeout } from "./admission-window";
import { registration } from "../tests/support/inputs";
import { seedPresentation, selectLanguage, ui } from "./localization-support";
import { casePath, referencePath } from "../src/routing/routes";
import { randomUUID } from "node:crypto";
import { expect, test, type Request } from "./fixtures";
import { openAuthenticated, authenticateSteward, expectAccessible } from "./session-helpers";
import { openCase, reviewDraft, startOpen, recordReviewed, startAct } from "./case-workflow";
import { fillBusinessField } from "./typed-entry";
import { recoveryRecords } from "./recovery-storage";

let stewardCookies: Awaited<ReturnType<typeof authenticateSteward>>;
test.beforeAll(async ({ browser, baseURL }) => {
  test.setTimeout(freshLoginWorkflowTimeout);
  stewardCookies = await authenticateSteward(browser, baseURL);
});
const plan = async (page: Parameters<typeof openCase>[0], action: string, due: string) => {
  await page
    .getByRole("button", {
      name: "Add reminder",
      exact: true,
    })
    .click();
  await fillBusinessField(page, "reminderAction", action);
  await fillBusinessField(page, "reminderDueDate", due);
  await reviewDraft(page);
  const receipt = await recordWorkReview(page, "REMINDER");
  // The raw fixture is a separate client; observe the accepted current page before its next request.
  const target = await acceptedSubject(page, receipt.operationId);
  if (target.kind !== "REMINDER" || target.effects.kind !== "SINGLE") {
    throw new Error("Expected the accepted reminder creation identity.");
  }
  const { reminderId } = target.effects.reminder;
  expect(reminderId).toBe(receipt.operationId);
  await expect(page.locator(`[id="reminder-${reminderId}-primary"]`)).toBeVisible();
  return receipt;
};
test("stores exact optional LF incident text and clears it without a financial assertion", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `NARRATIVE-${randomUUID().slice(0, 8)}`;
  await startOpen(page, reference);
  const description = `حادث في الطريق\n\n${"😀".repeat(584)}`;
  expect(Array.from(description)).toHaveLength(600);
  await fillBusinessField(page, "incidentDescription", description);
  await expect(page.locator('textarea[data-field-name="incidentDescription"]')).not.toHaveAttribute(
    "maxlength",
  );
  await reviewDraft(page);
  await recordReviewed(page);
  const recorded = await workRequest(page, "case.get", {
    target: {
      kind: "REFERENCE",
      caseReference: reference,
    },
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
  await expect(
    page.locator(".case-detail .case-area-claim [data-entry='incidentDescription']"),
  ).toContainText("حادث في الطريق");
  await startAct(page, "case.amendRegistration", "en");
  await page.locator('input[data-selects-fields~="incidentDescription"]').check();
  await page
    .getByRole("checkbox", {
      name: "Clear recorded value",
      exact: true,
    })
    .check();
  await reviewDraft(page);
  await recordReviewed(page);
  const cleared = await workRequest(page, "case.get", {
    target: {
      kind: "REFERENCE",
      caseReference: reference,
    },
  });
  if (cleared.outcome.kind !== "SUCCEEDED" || cleared.outcome.data.state.kind === "ERASED") {
    throw new Error("Expected live case");
  }
  expect(cleared.outcome.data.state.fields.registration.incidentDescription ?? null).toBeNull();
  expect(cleared.outcome.data.state.fields.finance.decision).toBeNull();
  expect(cleared.outcome.data.state.fields.finance).toMatchObject({
    totals: [],
    paymentCount: 0,
  });
});
test("supports multiple independently revised reminders, terminal records, reopen and last-completion attention", async ({
  page,
}) => {
  // This complete lifecycle includes multiple writes, independent DATA reads and an accessibility scan.
  test.slow();
  await openAuthenticated(page);
  const reference = `REMINDERS-${randomUUID().slice(0, 8)}`;
  await openCase(page, reference);
  const first = await plan(page, "Call claimant", "0001-01-01");
  const second = await plan(page, "Ask insurer", "9999-12-31");
  expect(first.operationId).not.toBe(second.operationId);
  await expect(
    page.locator(
      ".case-reminders .next-action-controls, .reminder-list .table-view tbody > tr:not(.table-group)",
    ),
  ).toHaveCount(2);
  await expect(
    reminderRows(page)
      .filter({
        hasText: "Call claimant",
      })
      .locator("[data-column='timing']"),
  ).toHaveText("Overdue");
  await expect(
    reminderRows(page)
      .filter({
        hasText: "Ask insurer",
      })
      .locator("[data-column='timing']"),
  ).toHaveText("Upcoming");
  await reviewReminderCompletion(
    page,
    reminderRows(page).filter({
      hasText: "Call claimant",
    }),
  );
  await recordWorkReview(page, "REMINDER");
  await selectReminderAction(
    reminderRows(page).filter({
      hasText: "Ask insurer",
    }),
    "reminder.cancel",
  );
  // Cancellation has no editable inputs and Core declares automatic preview.
  await recordWorkReview(page, "REMINDER");
  await expect(page.locator(".case-title-line .chip-list")).toContainText("No next action");
  await selectCaseReminderState(page, "COMPLETED");
  await expect(reminderRows(page)).toHaveCount(1);
  await expect(page.locator(".reminder-list")).not.toContainText("Overdue");
  await selectReminderAction(reminderRows(page), "reminder.reopen");
  // A supplied seed remains editable before the explicit review.
  await fillBusinessField(page, "reminderDueDate", "9999-12-30");
  await reviewDraft(page);
  await recordWorkReview(page, "REMINDER");
  await selectCaseReminderState(page, "PENDING");
  await expect(reminderRows(page)).toHaveCount(1);
  const current = await workRequest(page, "case.get", {
    target: {
      kind: "REFERENCE",
      caseReference: reference,
    },
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
  const reference = `COMPOUND-${randomUUID().slice(0, 8)}`;
  await openCase(page, reference);
  await plan(page, "First action", "2026-01-01");
  await selectReminderAction(reminderRows(page), "reminder.completeAndPlanNext");
  await fillBusinessField(page, "reminderAction", "   ");
  await fillBusinessField(page, "reminderDueDate", "9999-12-31");
  const invalid = page.waitForResponse(
    (value) => new URL(value.url()).pathname === "/api/operation.preview",
  );
  await page
    .getByRole("dialog")
    .getByRole("button", {
      name: "Review changes",
      exact: true,
    })
    .click();
  const operationPreviewReply = (await (await invalid).json()) as ResponseFor<
    "operation.preview",
    "HUMAN"
  >;
  if (operationPreviewReply.representation !== "HUMAN") {
    throw new Error("Expected compound validation result");
  }
  expect(operationPreviewReply.outcome).toMatchObject({
    kind: "REJECTED",
    rejection: {
      kind: "INPUT",
      issues: [
        {
          input: "input:reminderAction",
          row: null,
          message: {
            messageId: "diagnostic.TEXT_REQUIRED",
          },
        },
      ],
    },
  });
  const unchanged = await workRequest(page, "reminder.list", {
    target: {
      kind: "REFERENCE",
      caseReference: reference,
    },
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
  const target = await acceptedSubject(page, receipt.operationId);
  if (target.kind !== "REMINDER" || target.effects.kind !== "COMPLETED_AND_PLANNED") {
    throw new Error("Expected bounded pair");
  }
  expect(target.effects.next.reminderId).toBe(receipt.operationId);
  expect(target.effects.next.revision).toBe(1);
  expect(target.effects.completed.revision).toBe(2);
  await expect(reminderRows(page)).toHaveCount(1);
  await expect(reminderRows(page)).toContainText("Second action");
});
test("a lost compound acknowledgement retries the identical pair without another reminder", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `LOST-COMPOUND-${randomUUID().slice(0, 8)}`;
  await openCase(page, reference);
  await plan(page, "Current action", "2026-01-01");
  await selectReminderAction(reminderRows(page), "reminder.completeAndPlanNext");
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
      const operationExecuteReply = (await accepted.json()) as ResponseFor<
        "operation.execute",
        "HUMAN"
      >;
      if (
        operationExecuteReply.representation !== "HUMAN" ||
        operationExecuteReply.outcome.kind !== "ACCEPTED"
      ) {
        throw new Error("Expected accepted compound before acknowledgement loss");
      }
      await route.abort("connectionreset");
    }
  });
  await workConfirmButton(page).click();
  await page
    .getByRole("button", {
      name: "Send again",
      exact: true,
    })
    .click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  expect(captured).toHaveLength(2);
  expect(captured[1]).toBe(captured[0]);
  await expectCaseReadsSettled(page);
  const replacement = reminderRows(page);
  await expect(replacement).toHaveCount(1);
  await expect(replacement).toContainText("Replacement after uncertainty");
  const reminders = await workRequest(page, "reminder.list", {
    target: {
      kind: "REFERENCE",
      caseReference: reference,
    },
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
      const locale = (
        {
          en: "en-GB",
          lv: "lv-LV",
          ar: "ar-EG",
        } as const
      )[language];
      await page.setViewportSize({
        width: {
          "wide-light": 1440,
          "zoom-dark": 640,
          "narrow-dark": 390,
        }[mode],
        height: 900,
      });
      await page.emulateMedia({
        colorScheme: mode === "wide-light" ? "light" : "dark",
        reducedMotion: "reduce",
      });
      await seedPresentation(page, language, locale);
      await openAuthenticated(page, ui(language, "label.cases"));
      const caseId = mintOperationId();
      const reference = `LOCAL-WORK-${randomUUID().slice(0, 8)}`;
      const prefix = "الملخص\n\n";
      const text = `${prefix}${"😀".repeat(600 - Array.from(prefix).length)}`;
      const opened = await workRequest(page, "operation.execute", {
        operationId: caseId,
        task: "case.open",
        binding: {
          kind: "CASE",
          expectedRevision: 0,
        },
        inputs: {
          "field:caseReference": reference,
          ...prefixedInputs(
            "field:",
            registration({
              incidentDescription: text,
            }),
          ),
        },
      });
      expect(opened.outcome.kind).toBe("ACCEPTED");
      await page.goto(casePath(caseId));
      if (mode === "zoom-dark") {
        await page.evaluate(() => {
          document.documentElement.style.zoom = "2";
        });
      }
      await page.locator("#case-work-summary").click();
      await page.locator('input[data-selects-fields~="handlingSummary"]').check();
      await fillBusinessField(page, "handlingSummary", text);
      await page
        .getByRole("dialog")
        .getByRole("button", {
          name: ui(language, "label.reviewTitle"),
          exact: true,
        })
        .click();
      await expect(
        page.locator(".ui-dialog .review-values").filter({
          visible: true,
        }),
      ).toBeVisible();
      await recordWorkReview(page, "WORK");
      await page
        .locator(".case-reminders")
        .getByRole("button", {
          name: ui(language, "task.reminder.create.label"),
          exact: true,
        })
        .click();
      await fillBusinessField(page, "reminderAction", "😀".repeat(200));
      await fillBusinessField(page, "reminderDueDate", "9999-12-31");
      await page
        .getByRole("dialog")
        .getByRole("button", {
          name: ui(language, "label.reviewTitle"),
          exact: true,
        })
        .click();
      await recordWorkReview(page, "REMINDER");
      await expect(page.locator("html")).toHaveAttribute("dir", language === "ar" ? "rtl" : "ltr");
      await expect(
        page.locator(".entries-view [data-entry='handlingSummary'] .plain-text-value"),
      ).toHaveText(text);
      await expect(reminderRows(page)).toHaveCount(1);
      expect(
        await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
      ).toBe(true);
      const title = page.locator("#case-detail-title");
      await expect(title).toHaveText(reference);
      if (mode === "narrow-dark") {
        await expect(page.locator(".phone-app-bar")).toContainText(reference);
      } else {
        await expect(title).toBeVisible();
      }
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
  const reference = `PAID-WORK-${randomUUID().slice(0, 8)}`;
  await openCase(page, reference);
  await plan(page, "Finish existing action", "2026-01-01");
  for (const [revision, act] of [
    [
      1,
      {
        task: "case.decide",
        inputs: {
          "field:paymentDecisionDate": "2026-09-03",
          "field:payableAmount": "500",
          "field:payableCurrency": "EUR",
        },
      },
    ],
    [
      2,
      {
        task: "case.recordPayment",
        inputs: {
          "field:paymentDate": "2026-09-04",
          "field:paidAmount": "500",
          "field:paidCurrency": "EUR",
        },
      },
    ],
  ] as const) {
    const seeded = await workRequest(page, "operation.execute", {
      operationId: mintOperationId(),
      ...act,
      binding: {
        kind: "CASE",
        target: { kind: "REFERENCE", caseReference: reference },
        expectedRevision: revision,
      },
    });
    expect(seeded.outcome.kind).toBe("ACCEPTED");
  }
  await page.reload();
  await startAct(page, "case.correct", "en");
  await chooseChoice(page, "group:registration", "UPDATE");
  await page.locator('input[data-selects-fields~="incidentDescription"]').check();
  const description = "Paid case description\nNo new payment assertion.";
  await fillBusinessField(page, "incidentDescription", description);
  const advisory = page.waitForResponse(
    (value) => new URL(value.url()).pathname === "/api/operation.preview",
  );
  await reviewDraft(page);
  const requested: unknown = (await advisory).request().postDataJSON();
  expect(requested).toMatchObject({
    task: "case.correct",
    inputs: {
      "group:registration": "UPDATE",
      "field:incidentDescription": description,
      "group:decision": "KEEP",
    },
  });
  await recordReviewed(page);
  const closed = await workRequest(page, "operation.execute", {
    operationId: mintOperationId(),
    task: "case.close",
    binding: {
      kind: "CASE",
      target: {
        kind: "REFERENCE",
        caseReference: reference,
      },
      expectedRevision: 4,
    },
    inputs: { "field:outcome": "OTHER" },
  });
  expect(closed.outcome.kind).toBe("ACCEPTED");
  await page.reload();
  await page.locator("#case-work-summary").click();
  await expect(page.locator('[name="input:responsibleActorAction"]')).toHaveCount(0);
  await page.locator('input[data-selects-fields~="handlingSummary"]').check();
  await fillBusinessField(
    page,
    "handlingSummary",
    "Closed; awaiting confirmation.\nKeep the accepted payment facts.",
  );
  await reviewDraft(page);
  await expect(
    page
      .locator('.case-workspace [data-region="PAYMENTS"] .entries-view [data-entry="payable"]')
      .first(),
  ).toContainText("500.00");
  await expect(
    page.locator('.case-workspace [data-region="PAYMENTS"] [data-column="paymentDate"]').first(),
  ).toContainText("04/09/2026");
  await recordWorkReview(page, "WORK");
  await expect(
    page.locator(".case-reminders").getByRole("button", {
      name: "Add reminder",
      exact: true,
    }),
  ).toHaveCount(0);
  const pending = reminderRows(page);
  await expect(pending).toHaveCount(1);
  await expect(pending).toContainText(ui("en", "due.INACTIVE.label"));
  await expect(pending.locator("[data-column='timing']")).toHaveText(
    ui("en", "due.INACTIVE.label"),
  );
  await pending.locator("button[aria-haspopup]").click();
  await expect(page.locator('[data-menu-item="reminder.cancel"]')).toBeVisible();
  await expect(page.locator('[data-menu-item="reminder.update"]')).toHaveCount(0);
  await page.keyboard.press("Escape");
  await reviewReminderCompletion(page, pending);
  await recordWorkReview(page, "REMINDER");
  const current = await workRequest(page, "case.get", {
    target: {
      kind: "REFERENCE",
      caseReference: reference,
    },
  });
  if (current.outcome.kind !== "SUCCEEDED" || current.outcome.data.state.kind === "ERASED") {
    throw new Error("Expected closed paid case");
  }
  expect(current.outcome.data.revision).toBe(5);
  expect(current.outcome.data.state.fields.registration.incidentDescription).toBe(description);
  expect(current.outcome.data.state.fields.finance.decision).toEqual({
    paymentDecisionDate: "2026-09-03",
    payableAmount: "500",
    payableCurrency: "EUR",
  });
  expect(current.outcome.data.state.fields.finance).toMatchObject({
    totals: [{ currency: "EUR", paid: "500", returned: "0" }],
    paymentCount: 1,
  });
  expect(current.outcome.data.latestPayments).toMatchObject([
    {
      paymentDate: "2026-09-04",
      direction: "PAID",
      money: { amount: "500", currency: "EUR" },
    },
  ]);
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
  const reference = `ACTION-LF-${randomUUID().slice(0, 8)}`;
  await openCase(page, reference);
  await page
    .getByRole("button", {
      name: "Add reminder",
      exact: true,
    })
    .click();
  const action = page.locator('textarea[data-field-name="reminderAction"]');
  await action.fill("first\nsecond");
  await expect(action).toHaveValue("first\nsecond");
  await fillBusinessField(page, "reminderDueDate", "9999-12-31");
  const advisory = page.waitForResponse(
    (value) => new URL(value.url()).pathname === "/api/operation.preview",
  );
  await page
    .getByRole("dialog")
    .getByRole("button", {
      name: "Review changes",
      exact: true,
    })
    .click();
  const reply = await advisory;
  const input: unknown = reply.request().postDataJSON();
  expect(input).toMatchObject({
    task: "reminder.create",
    binding: { kind: "REMINDER" },
    inputs: { "input:reminderAction": "first\nsecond" },
  });
  const operationPreviewReply = (await reply.json()) as ResponseFor<"operation.preview", "HUMAN">;
  if (operationPreviewReply.representation !== "HUMAN") {
    throw new Error("Expected protocol refusal");
  }
  expect(operationPreviewReply.outcome).toMatchObject({
    kind: "REJECTED",
    rejection: {
      kind: "INPUT",
      issues: [
        {
          input: "input:reminderAction",
          row: null,
          message: {
            messageId: "diagnostic.CONTROL_CHARACTERS",
          },
        },
      ],
    },
  });
  await expect(page.getByRole("dialog").locator("[data-error-summary]")).toBeVisible();
  const reminders = await workRequest(page, "reminder.list", {
    target: {
      kind: "REFERENCE",
      caseReference: reference,
    },
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
  const reference = `STALE-COMPOUND-${randomUUID().slice(0, 8)}`;
  await openCase(page, reference);
  const initial = await plan(page, "Original action", "2026-01-01");
  await selectReminderAction(reminderRows(page), "reminder.completeAndPlanNext");
  await fillBusinessField(page, "reminderAction", "Planned replacement");
  await fillBusinessField(page, "reminderDueDate", "9999-12-31");
  const previewed = page.waitForResponse(
    (value) => new URL(value.url()).pathname === "/api/operation.preview",
  );
  const reviewed = await reviewDraft(page);
  const frozenBytes = (await previewed).request().postData();
  const concurrent = await workRequest(page, "operation.execute", {
    operationId: mintOperationId(),
    task: "reminder.update",
    binding: {
      kind: "REMINDER",
      target: {
        kind: "REFERENCE",
        caseReference: reference,
      },
      reminderId: initial.operationId,
      expectedRevision: 1,
    },
    inputs: {
      "input:reminderAction": "Changed by another session",
      "input:reminderDueDate": "2026-11-10",
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
    task: "reminder.completeAndPlanNext",
    binding: { kind: "REMINDER", reminderId: initial.operationId, expectedRevision: 1 },
    inputs: {},
  });
  const operationExecuteReply = (await response.json()) as ResponseFor<
    "operation.execute",
    "HUMAN"
  >;
  if (operationExecuteReply.representation !== "HUMAN") {
    throw new Error("Expected conflict result");
  }
  expect(operationExecuteReply.outcome).toMatchObject({
    kind: "REJECTED",
    rejection: {
      kind: "CONFLICT",
      actualRevision: 2,
    },
  });
  if (
    operationExecuteReply.outcome.kind !== "REJECTED" ||
    operationExecuteReply.outcome.rejection.kind !== "CONFLICT"
  ) {
    throw new Error("Expected the authoritative reminder revision conflict.");
  }
  const changes = operationExecuteReply.outcome.rejection.document;
  expect(changes.disclosure).toBe("FULL");
  const changedOperations = changes.screen.regions.flatMap((region) => {
    if (region.content.kind !== "TIMELINE") {
      return [];
    }
    return region.content.entries.flatMap((entry) =>
      entry.details.flatMap((section) =>
        section.entries.flatMap((fact) =>
          fact.key === "operationId" && fact.value.kind === "IDENTIFIER" ? [fact.value.value] : [],
        ),
      ),
    );
  });
  expect(changedOperations).toContain(concurrent.outcome.data.operationId);
  const conflict = page.getByRole("dialog").locator(".revision-conflict");
  await expect(
    conflict.getByRole("heading", {
      name: "This case has changed",
      exact: true,
    }),
  ).toBeVisible();
  await expect(conflict).toContainText("Your draft is kept.");
  await expect(conflict).toContainText("Changed by another session");
  expect(await recoveryRecords(page)).toEqual([]);
  await expect(
    conflict.getByRole("button", {
      name: "Review against latest",
      exact: true,
    }),
  ).toBeEnabled();
  const current = await workRequest(page, "reminder.list", {
    target: {
      kind: "REFERENCE",
      caseReference: reference,
    },
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
    target: {
      kind: "REFERENCE",
      caseReference: reference,
    },
    reminderId: reviewed.operationId,
  });
  expect(replacement.outcome.kind).toBe("REJECTED");
});
test("published reminder rows show server timing in case and queue views across languages", async ({
  page,
  browser,
}) => {
  await openAuthenticated(page);
  const reference = `TIMING-${randomUUID().slice(0, 8)}`;
  await openCase(page, reference);
  const observed = await workRequest(page, "work.views", {});
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
      operationId: mintOperationId(),
      task: "reminder.create",
      binding: {
        kind: "REMINDER",
        target: {
          kind: "REFERENCE",
          caseReference: reference,
        },
      },
      inputs: { "input:reminderAction": actions[index]!, "input:reminderDueDate": dueDate },
    });
    expect(accepted.outcome.kind).toBe("ACCEPTED");
  }
  await page.clock.setFixedTime(new Date("2031-06-15T12:00:00Z"));
  await page.reload();
  for (const language of ["en", "lv", "ar"] as const) {
    await selectLanguage(page, language);
    for (const [index, timing] of (["OVERDUE", "TODAY", "UPCOMING"] as const).entries()) {
      const item = reminderRows(page).filter({
        hasText: actions[index]!,
      });
      await expect(item.locator("[data-column='timing']")).toHaveText(
        ui(language, `reminderDue.${timing}.label`),
      );
      await expect(page.locator('.case-reminders a[href^="/reminders"]')).toBeVisible();
      await expect(page.locator(".shell-business-date")).toContainText(timeZone);
    }
  }
  await page.goto("/reminders");
  const reminderSearch = page.getByRole("searchbox", {
    name: ui("ar", "ui.worklistSearch"),
    exact: true,
  });
  await reminderSearch.fill(reference);
  await reminderSearch.press("Enter");
  await expect(reminderRows(page)).toHaveCount(3);
  for (const [index, label] of ["متأخر", "اليوم", "قادم"].entries()) {
    const item = reminderRows(page).filter({
      hasText: actions[index]!,
    });
    await expect(item.locator("..").locator('th[scope="rowgroup"]')).toContainText(label);
    const due = item.locator('[data-column="due"]');
    await expect(due.locator("time")).toHaveCount(1);
    await expect(due.locator(".chip")).toHaveCount(0);
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
      operationId: mintOperationId(),
      task: "case.void",
      binding: {
        kind: "CASE",
        target: {
          kind: "REFERENCE",
          caseReference: reference,
        },
        expectedRevision: 1,
      },
      inputs: { "input:reason": "Synthetic timing test entry error" },
    });
    expect(voided.outcome.kind).toBe("ACCEPTED");
  } finally {
    await stewardContext.close();
  }
  await page.reload();
  await reminderSearch.fill(reference);
  await reminderSearch.press("Enter");
  await expect(reminderRows(page)).toHaveCount(0);
  const filters = page.locator('.reminder-queue button[aria-haspopup="dialog"]');
  await filters.click();
  const sheet = page.locator(".filter-sheet").getByRole("dialog");
  const inactive = sheet.getByRole("checkbox", {
    name: ui("ar", "input.includeInactive.label"),
  });
  await expect(inactive).toBeEnabled();
  await expect(inactive).toBeChecked();
  await sheet
    .getByRole("button", {
      name: ui("ar", "ui.manualFilters"),
      exact: true,
    })
    .click();
  await expect(page).toHaveURL(
    (url) => url.searchParams.get("filters") === "manual" && !url.searchParams.has("view"),
  );
  await filters.click();
  await inactive.check();
  await sheet.locator('button[type="submit"]').click();
  await expect(reminderRows(page)).toHaveCount(3);
  await expect(page.locator(".reminder-list")).not.toContainText(
    ui("ar", "reminderDue.OVERDUE.label"),
  );
  for (const item of await reminderRows(page).all()) {
    await expect(item.locator("..").locator('th[scope="rowgroup"]')).toContainText(
      ui("ar", "due.INACTIVE.label"),
    );
    await expect(item.locator('[data-column="due"] .chip')).toHaveCount(0);
  }
  const retained = await workRequest(page, "reminder.list", {
    includeInactive: true,
    text: reference,
    limit: 50,
  });
  if (retained.outcome.kind !== "SUCCEEDED") {
    throw new Error("Expected retained reminders.");
  }
  expect(retained.outcome.data.items.map((row) => row.reminder.state)).toEqual([
    "PENDING",
    "PENDING",
    "PENDING",
  ]);
});

const seedReminder = async (
  page: Parameters<typeof openCase>[0],
  caseReference: string,
  action: string,
  due: string,
) => {
  const operationId = mintOperationId();
  const result = await workRequest(page, "operation.execute", {
    operationId,
    task: "reminder.create",
    binding: { kind: "REMINDER", target: { kind: "REFERENCE", caseReference } },
    inputs: { "input:reminderAction": action, "input:reminderDueDate": due },
  });
  expect(result.outcome.kind).toBe("ACCEPTED");
  return { operationId };
};

test("@engine exact reminder URLs retain completed selection and the case reference across reload", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `EXACT-${randomUUID().slice(0, 8)}`;
  await openCase(page, reference);
  const first = await seedReminder(page, reference, "Exact completed reminder", "2026-01-01");
  const second = await seedReminder(page, reference, "Another pending reminder", "2026-01-02");
  const target = { kind: "REFERENCE" as const, caseReference: reference };
  const completed = await workRequest(page, "operation.execute", {
    operationId: mintOperationId(),
    task: "reminder.complete",
    binding: { kind: "REMINDER", target, reminderId: first.operationId, expectedRevision: 1 },
    inputs: {},
  });
  expect(completed.outcome.kind).toBe("ACCEPTED");
  const earlierId = mintOperationId();
  const earlier = await workRequest(page, "operation.execute", {
    operationId: earlierId,
    task: "reminder.create",
    binding: { kind: "REMINDER", target },
    inputs: {
      "input:reminderAction": "Earlier completed reminder",
      "input:reminderDueDate": "2025-01-01",
    },
  });
  expect(earlier.outcome.kind).toBe("ACCEPTED");
  const earlierCompleted = await workRequest(page, "operation.execute", {
    operationId: mintOperationId(),
    task: "reminder.complete",
    binding: { kind: "REMINDER", target, reminderId: earlierId, expectedRevision: 1 },
    inputs: {},
  });
  expect(earlierCompleted.outcome.kind).toBe("ACCEPTED");
  const firstPage = await workRequest(page, "reminder.list", {
    target,
    state: "COMPLETED",
    limit: 1,
  });
  if (firstPage.outcome.kind !== "SUCCEEDED") {
    throw new Error("Expected independently observed first reminder page");
  }
  expect(firstPage.outcome.data.items.map((item) => item.reminder.reminderId)).toEqual([earlierId]);
  expect(firstPage.outcome.data.nextCursor).not.toBeNull();
  // The selected terminal resource is demonstrably beyond that first page; the UI must read it directly.

  await page.goto(`${referencePath(reference)}/reminders/${first.operationId}`);
  await expectCaseReadsSettled(page);
  const selected = page.locator(".selected-reminder");
  await expect(selected).toContainText("Exact completed reminder");
  await expect(selected).toContainText("Completed");
  await expect(selected).not.toContainText("Another pending reminder");
  await page.reload();
  await expectCaseReadsSettled(page);
  await expect(selected).toContainText("Exact completed reminder");
  const reopen = selected.getByRole("button", { name: /^Reopen reminder/u });
  await reopen.click();
  const drawer = page.getByRole("dialog");
  await expect(drawer.getByRole("button", { name: "Review changes", exact: true })).toBeVisible();
  await fillBusinessField(page, "reminderDueDate", "9999-12-30");
  await drawer.getByRole("button", { name: "Close", exact: true }).first().click();
  await page
    .getByRole("dialog", { name: "Discard these draft changes?" })
    .getByRole("button", { name: "Discard and leave", exact: true })
    .click();
  await expect(reopen).toBeFocused();
  await reopen.click();
  await reviewDraft(page);
  const dispatched = page.waitForRequest(
    (request) => new URL(request.url()).pathname === "/api/operation.execute",
  );
  await recordWorkReview(page, "REMINDER");
  expect((await dispatched).postDataJSON()).toMatchObject({
    task: "reminder.reopen",
    binding: { kind: "REMINDER", reminderId: first.operationId, expectedRevision: 2 },
  });
  await expectCaseReadsSettled(page);
  await expect(selected.getByRole("button", { name: /^Complete reminder/u })).toBeVisible();

  await page.goto(`${referencePath(reference)}/reminders/${second.operationId}`);
  await expectCaseReadsSettled(page);
  await expect(selected).toContainText("Another pending reminder");
  await expect(selected).not.toContainText("Exact completed reminder");
  await page.setViewportSize({ width: 360, height: 760 });
  await selectLanguage(page, "ar");
  await expectCaseReadsSettled(page);
  await expect(selected.locator('[data-act="reminder.complete"]')).toContainText(
    ui("ar", "task.reminder.complete.label"),
  );
  await noHorizontalOverflow(page, "narrow");
  await expectAccessible(page);
});

test("@engine returns from an exact reminder to its filtered queue scroll and clicked focus", async ({
  page,
}) => {
  await page.setViewportSize({ width: 640, height: 500 });
  await openAuthenticated(page);
  const reference = `QUEUE-RETURN-${randomUUID().slice(0, 8)}`;
  await openCase(page, reference);
  let last = "";
  for (let index = 0; index < 12; index += 1) {
    last = mintOperationId();
    const reply = await workRequest(page, "operation.execute", {
      operationId: last,
      task: "reminder.create",
      binding: { kind: "REMINDER", target: { kind: "REFERENCE", caseReference: reference } },
      inputs: {
        "input:reminderAction": `${reference} row ${index}`,
        "input:reminderDueDate": "2026-01-01",
      },
    });
    expect(reply.outcome.kind).toBe("ACCEPTED");
  }
  await page.goto(`/reminders?view=PENDING&q=${encodeURIComponent(reference)}`);
  const queue = page.url();
  const link = page.locator(`[id="reminder-link-${last}"]`);
  await expect(link).toBeVisible();
  await link.scrollIntoViewIfNeeded();
  await link.focus();
  const position = await page.evaluate(() => scrollY);
  expect(position).toBeGreaterThan(0);
  await link.click();
  await expect(page).toHaveURL(new RegExp(`/reminders/${last}$`, "u"));
  await expectCaseReadsSettled(page);
  await expect(page.locator(".selected-reminder")).toContainText(`${reference} row 11`);
  await page.goBack();
  await expect(page).toHaveURL(queue);
  await expect(link).toBeFocused();
  await expect.poll(() => page.evaluate(() => scrollY)).toBe(position);
});

test("@engine exact missing and wrong-parent reminders settle without parent refresh loops", async ({
  page,
}) => {
  await openAuthenticated(page);
  const first = `FOREIGN-${randomUUID().slice(0, 8)}`;
  await openCase(page, first);
  const foreign = await seedReminder(page, first, "Private foreign reminder", "2026-01-01");
  const parent = `READABLE-${randomUUID().slice(0, 8)}`;
  await page.goto("/cases");
  await openCase(page, parent);
  for (const id of [foreign.operationId, randomUUID()]) {
    const calls = { parent: 0, reminder: 0 };
    const count = (request: Request) => {
      const path = new URL(request.url()).pathname;
      if (path === "/api/case.get") {
        calls.parent += 1;
      }
      if (path === "/api/reminder.get") {
        calls.reminder += 1;
      }
    };
    page.on("request", count);
    await page.goto(`${referencePath(parent)}/reminders/${id}`);
    const selected = page.locator(".selected-reminder");
    await expect(selected.getByRole("alert")).toBeVisible();
    await expect.poll(() => calls).toEqual({ parent: 2, reminder: 2 });
    // A bounded quiet interval catches the former parent-loading/remount cycle.
    await page.waitForTimeout(250);
    expect(calls).toEqual({ parent: 2, reminder: 2 });
    await expect(selected).not.toContainText("Private foreign reminder");
    await expect(selected.getByRole("button")).toHaveCount(0);
    page.off("request", count);
  }
});

test("completing a list reminder focuses its receipt banner", async ({ page }) => {
  await openAuthenticated(page);
  const reference = `FEEDBACK-${randomUUID().slice(0, 8)}`;
  await openCase(page, reference);
  const action = `Confirm the synthetic delivery ${reference}`;
  const operationId = mintOperationId();
  const reply = await workRequest(page, "operation.execute", {
    operationId,
    task: "reminder.create",
    binding: { kind: "REMINDER", target: { kind: "REFERENCE", caseReference: reference } },
    inputs: {
      "input:reminderAction": action,
      "input:reminderDueDate": "2026-01-01",
    },
  });
  expect(reply.outcome.kind).toBe("ACCEPTED");
  await page.goto(`/reminders?view=PENDING&q=${encodeURIComponent(reference)}`);
  const row = page.getByRole("row").filter({ hasText: action });
  await row.getByRole("button", { name: /^Complete reminder:/u }).click();
  await expect.poll(() => feedbackFailures(page, "Reminder completed.")).toEqual([]);
  await expect(row).toHaveCount(0);
  await expect(
    page.locator(".page > .status-banner").getByRole("button", { name: "View receipt" }),
  ).toBeVisible();
});
