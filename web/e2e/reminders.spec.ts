import { type ResponseFor } from "@contracts/client";
import { expectCaseReadsSettled } from "./case-readiness";
import { operationId as mintOperationId } from "../src/operation/operationId";
import { chooseChoice, settlePopupTriggerScroll } from "./workspace-navigation";
import { freshLoginWorkflowTimeout } from "./admission-window";
import { registration } from "../tests/support/inputs";
import { seedPresentation, selectLanguage, ui } from "./localization-support";
import { casePath } from "../src/routing/routes";
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
  const { target } = receipt.document.context;
  if (target.kind !== "REMINDER" || target.versions.length !== 1) {
    throw new Error("Expected the accepted reminder creation identity.");
  }
  const { reminderId } = target.versions[0]!;
  expect(reminderId).toBe(receipt.operationId);
  await expect(page.locator(`[id="reminder-${reminderId}-reminder.complete"]`)).toBeVisible();
  return receipt;
};
test("stores exact optional LF incident text and clears it without a financial assertion", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `NARRATIVE-${randomUUID()}`;
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
    page.locator(".case-detail .case-fact-regions [data-entry='incidentDescription']"),
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
    paidAmount: "0",
    paymentCount: 0,
    firstPaymentDate: null,
    lastPaymentDate: null,
  });
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
  await expect(
    page.locator(":is(.reminder-list,.case-reminders) .table-view tbody > tr:not(.table-group)"),
  ).toHaveCount(2);
  await expect(
    page
      .locator(":is(.reminder-list,.case-reminders) .table-view tbody > tr:not(.table-group)")
      .filter({
        hasText: "Call claimant",
      })
      .locator("[data-column='timing']"),
  ).toHaveText("Overdue");
  await expect(
    page
      .locator(":is(.reminder-list,.case-reminders) .table-view tbody > tr:not(.table-group)")
      .filter({
        hasText: "Ask insurer",
      })
      .locator("[data-column='timing']"),
  ).toHaveText("Upcoming");
  await reviewReminderCompletion(
    page,
    page
      .locator(":is(.reminder-list,.case-reminders) .table-view tbody > tr:not(.table-group)")
      .filter({
        hasText: "Call claimant",
      }),
  );
  await recordWorkReview(page, "REMINDER");
  await selectReminderAction(
    page,
    page
      .locator(":is(.reminder-list,.case-reminders) .table-view tbody > tr:not(.table-group)")
      .filter({
        hasText: "Ask insurer",
      }),
    "reminder.cancel",
  );
  await reviewDraft(page);
  await recordWorkReview(page, "REMINDER");
  await expect(page.locator('[data-region="ATTENTION"]')).toBeVisible();
  await selectCaseReminderState(page, "COMPLETED");
  await expect(
    page.locator(":is(.reminder-list,.case-reminders) .table-view tbody > tr:not(.table-group)"),
  ).toHaveCount(1);
  await expect(page.locator(".reminder-list")).not.toContainText("Overdue");
  await selectReminderAction(
    page,
    page.locator(":is(.reminder-list,.case-reminders) .table-view tbody > tr:not(.table-group)"),
    "reminder.reopen",
  );
  await fillBusinessField(page, "reminderDueDate", "9999-12-30");
  await reviewDraft(page);
  await recordWorkReview(page, "REMINDER");
  await selectCaseReminderState(page, "PENDING");
  await expect(
    page.locator(":is(.reminder-list,.case-reminders) .table-view tbody > tr:not(.table-group)"),
  ).toHaveCount(1);
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
  const reference = `COMPOUND-${randomUUID()}`;
  await openCase(page, reference);
  await plan(page, "First action", "2026-01-01");
  await selectReminderAction(
    page,
    page.locator(":is(.reminder-list,.case-reminders) .table-view tbody > tr:not(.table-group)"),
    "reminder.completeAndPlanNext",
  );
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
  const { target } = receipt.document.context;
  if (target.kind !== "REMINDER" || target.versions.length !== 2) {
    throw new Error("Expected bounded pair");
  }
  expect(target.versions[1]!.reminderId).toBe(receipt.operationId);
  expect(target.versions[1]!.revision).toBe(1);
  expect(target.versions[0]!.revision).toBe(2);
  await expect(
    page.locator(":is(.reminder-list,.case-reminders) .table-view tbody > tr:not(.table-group)"),
  ).toHaveCount(1);
  await expect(
    page.locator(":is(.reminder-list,.case-reminders) .table-view tbody > tr:not(.table-group)"),
  ).toContainText("Second action");
});
test("a lost compound acknowledgement retries the identical pair without another reminder", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `LOST-COMPOUND-${randomUUID()}`;
  await openCase(page, reference);
  await plan(page, "Current action", "2026-01-01");
  await selectReminderAction(
    page,
    page.locator(":is(.reminder-list,.case-reminders) .table-view tbody > tr:not(.table-group)"),
    "reminder.completeAndPlanNext",
  );
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
  const replacement = page.locator(
    ":is(.reminder-list,.case-reminders) .table-view tbody > tr:not(.table-group)",
  );
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
      await openAuthenticated(page, ui(language, "ui.cases"));
      const caseId = mintOperationId();
      const reference = `LOCAL-WORK-${randomUUID()}`;
      const prefix = "الملخص\n\n";
      const text = `${prefix}${"😀".repeat(600 - Array.from(prefix).length)}`;
      const opened = await workRequest(page, "operation.execute", {
        kind: "CASE",
        operationId: caseId,
        target: {
          kind: "REFERENCE",
          caseReference: reference,
        },
        expectedRevision: 0,
        act: {
          kind: "case.open",
          values: registration({
            incidentDescription: text,
          }),
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
      await expect(
        page.locator(
          ":is(.reminder-list,.case-reminders) .table-view tbody > tr:not(.table-group)",
        ),
      ).toHaveCount(1);
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
        kind: "case.decide",
        values: {
          paymentDecisionDate: "2026-09-03",
          payableAmount: "500",
          payableCurrency: "EUR",
        },
      },
    ],
    [
      2,
      {
        kind: "case.recordPayment",
        values: {
          paymentDate: "2026-09-04",
          paidAmount: "500",
          paidCurrency: "EUR",
        },
      },
    ],
  ] as const) {
    const seeded = await workRequest(page, "operation.execute", {
      kind: "CASE",
      operationId: mintOperationId(),
      target: {
        kind: "REFERENCE",
        caseReference: reference,
      },
      expectedRevision: revision,
      act,
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
    act: {
      kind: "case.correct",
      groups: {
        registration: {
          kind: "reminder.update",
          values: {
            incidentDescription: description,
          },
        },
        decision: {
          kind: "KEEP",
        },
      },
    },
  });
  await recordReviewed(page);
  const closed = await workRequest(page, "operation.execute", {
    kind: "CASE",
    operationId: mintOperationId(),
    target: {
      kind: "REFERENCE",
      caseReference: reference,
    },
    expectedRevision: 4,
    act: {
      kind: "case.close",
    },
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
  const pending = page.locator(
    ":is(.reminder-list,.case-reminders) .table-view tbody > tr:not(.table-group)",
  );
  await expect(pending).toHaveCount(1);
  await expect(pending).toContainText(ui("en", "due.INACTIVE.label"));
  await expect(pending.locator("[data-column='timing']")).toHaveText(
    ui("en", "due.INACTIVE.label"),
  );
  const overflow = pending.getByRole("button", {
    name: "Available actions",
    exact: false,
  });
  await settlePopupTriggerScroll(overflow);
  await overflow.click();
  const menu = page.getByRole("menu");
  await expect(
    menu.getByRole("menuitem", {
      name: "Cancel reminder",
      exact: true,
    }),
  ).toBeVisible();
  await expect(
    menu.getByRole("menuitem", {
      name: "Update reminder",
      exact: true,
    }),
  ).toHaveCount(0);
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
    paidAmount: "500",
    paymentCount: 1,
    firstPaymentDate: "2026-09-04",
    lastPaymentDate: "2026-09-04",
  });
  expect(current.outcome.data.latestPayments).toMatchObject([
    {
      paymentDate: "2026-09-04",
      paidAmount: "500",
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
  const reference = `ACTION-LF-${randomUUID()}`;
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
    kind: "REMINDER",
    act: {
      kind: "reminder.create",
      action: "first\nsecond",
    },
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
  const reference = `STALE-COMPOUND-${randomUUID()}`;
  await openCase(page, reference);
  const initial = await plan(page, "Original action", "2026-01-01");
  await selectReminderAction(
    page,
    page.locator(":is(.reminder-list,.case-reminders) .table-view tbody > tr:not(.table-group)"),
    "reminder.completeAndPlanNext",
  );
  await fillBusinessField(page, "reminderAction", "Planned replacement");
  await fillBusinessField(page, "reminderDueDate", "9999-12-31");
  const previewed = page.waitForResponse(
    (value) => new URL(value.url()).pathname === "/api/operation.preview",
  );
  const reviewed = await reviewDraft(page);
  const frozenBytes = (await previewed).request().postData();
  const concurrent = await workRequest(page, "operation.execute", {
    kind: "REMINDER",
    operationId: mintOperationId(),
    target: {
      kind: "REFERENCE",
      caseReference: reference,
    },
    act: {
      kind: "reminder.update",
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
    act: {
      kind: "reminder.completeAndPlanNext",
      reminderId: initial.operationId,
      expectedRevision: 1,
    },
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
  const reference = `TIMING-${randomUUID()}`;
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
      kind: "REMINDER",
      operationId: mintOperationId(),
      target: {
        kind: "REFERENCE",
        caseReference: reference,
      },
      act: {
        kind: "reminder.create",
        action: actions[index]!,
        dueDate,
      },
    });
    expect(accepted.outcome.kind).toBe("ACCEPTED");
  }
  await page.clock.setFixedTime(new Date("2031-06-15T12:00:00Z"));
  await page.reload();
  for (const language of ["en", "lv", "ar"] as const) {
    await selectLanguage(page, language);
    for (const [index, timing] of (["OVERDUE", "TODAY", "UPCOMING"] as const).entries()) {
      const item = page
        .locator(":is(.reminder-list,.case-reminders) .table-view tbody > tr:not(.table-group)")
        .filter({
          hasText: actions[index]!,
        });
      await expect(item.locator("[data-column='timing']")).toHaveText(
        ui(language, `reminderDue.${timing}.label`),
      );
      await expect(page.locator(".case-reminder-controls > summary")).toContainText(
        ui(language, "reminderState.PENDING.label"),
      );
      await expect(page.locator(".shell-business-date")).toContainText(timeZone);
    }
  }
  await page.goto("/reminders");
  const reminderSearch = page.getByRole("search", {
    name: ui("ar", "label.reminders"),
    exact: true,
  });
  await reminderSearch.getByRole("searchbox").fill(reference);
  await reminderSearch.getByRole("button").click();
  await expect(
    page.locator(":is(.reminder-list,.case-reminders) .table-view tbody > tr:not(.table-group)"),
  ).toHaveCount(3);
  for (const [index, label] of ["متأخر", "اليوم", "قادم"].entries()) {
    const item = page
      .locator(":is(.reminder-list,.case-reminders) .table-view tbody > tr:not(.table-group)")
      .filter({
        hasText: actions[index]!,
      });
    await expect(item.locator("[data-column='timing']")).toHaveText(label);
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
      operationId: mintOperationId(),
      target: {
        kind: "REFERENCE",
        caseReference: reference,
      },
      expectedRevision: 1,
      act: {
        kind: "case.void",
        reason: "Synthetic timing test entry error",
      },
    });
    expect(voided.outcome.kind).toBe("ACCEPTED");
  } finally {
    await stewardContext.close();
  }
  await page.reload();
  await reminderSearch.getByRole("searchbox").fill(reference);
  await reminderSearch.getByRole("button").click();
  await expect(
    page.locator(":is(.reminder-list,.case-reminders) .table-view tbody > tr:not(.table-group)"),
  ).toHaveCount(0);
  const inactive = page.getByRole("checkbox", {
    name: ui("ar", "input.includeInactive.label"),
  });
  await expect(inactive).toBeDisabled();
  await page
    .getByRole("button", {
      name: ui("ar", "ui.manualFilters"),
      exact: true,
    })
    .click();
  await expect(page).toHaveURL(
    (url) => url.searchParams.get("filters") === "manual" && !url.searchParams.has("view"),
  );
  await inactive.check();
  await expect(
    page.locator(":is(.reminder-list,.case-reminders) .table-view tbody > tr:not(.table-group)"),
  ).toHaveCount(3);
  await expect(page.locator(".reminder-list")).not.toContainText("Overdue");
  for (const item of await page
    .locator(":is(.reminder-list,.case-reminders) .table-view tbody > tr:not(.table-group)")
    .all()) {
    await expect(item).toContainText(ui("ar", "due.INACTIVE.label"));
    await expect(item).toContainText(ui("ar", "reminderState.PENDING.label"));
  }
});
