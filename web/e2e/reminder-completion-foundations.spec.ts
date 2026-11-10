import { humanReviewEnvelope } from "../tests/support/human-review-response.fixtures";
import { showCaseRecordDetails } from "./workspace-navigation";
import { humanReadResponse } from "../tests/support/human-read-response.fixtures";
import { writeFile } from "node:fs/promises";
import { expect, test } from "@playwright/test";
import { captureCheckpoint, observe, seed } from "./presentation-probe";
import { expectAccessible } from "./session-helpers";
import { casePath } from "../src/routing/routes";
import { isRequest, isResponse } from "../src/protocol/client";
import { ui } from "./localization-support";
import { actorContextInput, actorRefInput, caseViewInput } from "../tests/support/inputs";
import { caseAdvice } from "../tests/support/case-advice.fixtures";
import { caseFields } from "../tests/support/service-foundation.fixtures";
import { timelinePage } from "../tests/support/timeline.fixtures";
import type { ReminderReadModel } from "@contracts/client";
const basis = {
  observedAt: "2026-11-04T12:00:00.000000Z",
  businessDate: "2026-11-04",
  timeZone: "Etc/UTC",
};
const current = caseViewInput({
  caseId: "20000000-0000-4000-8000-000000000001",
  caseReference: "CASE-1",
  revision: 3,
  latestPayments: [
    {
      paymentId: "10000000-0000-7000-8000-000000000003",
      paymentDate: "2026-10-12",
      paidAmount: "20000",
    },
  ],
  headline: "PAID",
  stages: {
    kind: "TRACKER",
    current: "PAID",
    steps: [
      {
        stage: "OPENED",
        completed: true,
        date: "2026-09-02",
        amount: { amount: "25000", currency: "AED" },
      },
      {
        stage: "DECIDED",
        completed: true,
        date: "2026-10-10",
        amount: { amount: "20000", currency: "AED" },
      },
      { stage: "PAID", completed: true, date: "2026-10-12", amount: null },
      { stage: "CLOSED", completed: false, date: null, amount: null },
    ],
  },
  advice: { ...caseAdvice(["CLOSE", "VOID", "ERASE"]), recommended: "CLOSE", history: "SUMMARY" },
  state: {
    kind: "ACTIVE",
    fields: {
      ...caseFields,
      finance: {
        decision: {
          paymentDecisionDate: "2026-10-10",
          payableAmount: "20000",
          payableCurrency: "AED",
        },
        paidAmount: "20000",
        paymentCount: 1,
        firstPaymentDate: "2026-10-12",
        lastPaymentDate: "2026-10-12",
      },
      registration: {
        ...caseFields.registration,
        claimedAmount: "25000",
        claimedCurrency: "AED",
        claimantName: "شركة الخليج للتجارة",
        incidentDescription: "Collision in the car park. No injuries.",
      },
    },
  },
});
const rows: ReminderReadModel[] = [
  {
    claimantName: "Synthetic claimant",
    responsible: null,
    reminder: {
      reminderId: "30000000-0000-4000-8000-000000000003",
      caseId: current.caseId,
      revision: 3,
      action: "Call claimant about invoice",
      dueDate: "2026-10-28",
      state: "PENDING",
    },
    caseReference: current.caseReference,
    recordedAt: basis.observedAt,
    recordedBy: actorRefInput(),
    acts: ["COMPLETE", "UPDATE", "CANCEL", "COMPLETE_AND_PLAN_NEXT"],
    active: true,
    timing: "OVERDUE",
  },
  {
    claimantName: "Synthetic claimant",
    responsible: null,
    reminder: {
      reminderId: "30000000-0000-4000-8000-000000000004",
      caseId: current.caseId,
      revision: 1,
      action: "Chase insurer for approval",
      dueDate: basis.businessDate,
      state: "PENDING",
    },
    caseReference: current.caseReference,
    recordedAt: basis.observedAt,
    recordedBy: actorRefInput(),
    acts: ["COMPLETE", "UPDATE", "CANCEL", "COMPLETE_AND_PLAN_NEXT"],
    active: true,
    timing: "TODAY",
  },
];
const contexts = [
  { language: "en", accent: "green", mode: "light", mobile: false },
  { language: "en", accent: "green", mode: "dark", mobile: false },
  { language: "ar", accent: "blue", mode: "dark", mobile: true },
] as const;
for (const { language, accent, mode, mobile } of contexts) {
  test(`populated reminder completion and focus safety in ${language} ${accent} ${mode}`, async ({
    page,
    browserName,
  }, testInfo) => {
    const clean = await observe(page, true);
    await seed(page, { language, accent, mode });
    await page.setViewportSize(
      mobile ? { width: 390, height: 844 } : { width: 1440, height: 1000 },
    );
    await page.route("**/api/context", (route) =>
      route.fulfill({
        json: {
          ...humanReadResponse("context", "SUCCEEDED", {
            requester: actorContextInput({
              productVersion: "synthetic-presentation",
              ...basis,
              capabilities: ["READ_CASE", "READ_HISTORY", "MANAGE_CASE_WORK", "CLOSE", "STEWARD"],
              views: ["CASES", "OPERATIONS"],
              offers: [],
            }),
          }),
        },
      }),
    );
    const view = {
      ...current,
      nextActions: rows,
      work: {
        ...current.work,
        basis,
        pendingReminders: 2,
        overdueReminders: 1,
        needsNextAction: false,
        canCreateReminder: true,
        canUpdateSummary: true,
        canAssign: true,
        summary: "Waiting for a repair invoice from the claimant.",
      },
    };
    await page.route("**/api/case.get", (route) =>
      route.fulfill({
        json: {
          ...humanReadResponse("case.get", "SUCCEEDED", view),
        },
      }),
    );
    await page.route("**/api/case.timeline", (route) =>
      route.fulfill({
        json: {
          ...humanReadResponse("case.timeline", "SUCCEEDED", {
            ...timelinePage([], null, "SUMMARY"),
            basis,
          }),
        },
      }),
    );
    await page.route("**/api/reminder.list", (route) =>
      route.fulfill({
        json: {
          ...humanReadResponse("reminder.list", "SUCCEEDED", {
            basis,
            items: rows,
            nextCursor: null,
          }),
        },
      }),
    );
    const requests: string[] = [];
    page.on("request", (request) => {
      if (request.url().includes("/api/")) {
        requests.push(new URL(request.url()).pathname);
      }
    });
    let previews = 0;
    let writes = 0;
    await page.route("**/api/operation.preview", (route) => {
      const input: unknown = route.request().postDataJSON();
      if (
        !isRequest("operation.preview", input) ||
        input.kind !== "REMINDER" ||
        input.act.kind !== "COMPLETE"
      ) {
        throw new Error("Expected one frozen reminder completion preview.");
      }
      expect(input.act).toEqual({
        kind: "COMPLETE",
        reminderId: rows[0]!.reminder.reminderId,
        expectedRevision: 3,
      });
      previews += 1;
      const reply = humanReviewEnvelope(
        input,
        {
          target: {
            kind: "REMINDER",
            caseId: current.caseId,
            existing: { reminderId: input.act.reminderId, revision: 3 },
          },
          confirmation: { kind: "CASE_REFERENCE", reference: "CASE-1" },
        },
        basis,
        {
          regions: [
            {
              role: "IDENTITY",
              content: {
                kind: "ENTRIES",
                entries: [
                  {
                    key: "caseReference",
                    labelId: "field.caseReference.label",
                    prominence: "PRIMARY",
                    value: { kind: "REFERENCE", value: "CASE-1" },
                  },
                  {
                    key: "reminderAction",
                    labelId: "field.reminderAction.label",
                    prominence: "PRIMARY",
                    value: { kind: "TEXT", value: "Call claimant about invoice" },
                  },
                ],
              },
            },
          ],
        },
        {
          changes: [
            {
              key: "state",
              labelId: "input.reminderState.label",
              before: { kind: "CODE", vocabulary: "reminderState", token: "PENDING" },
              after: { kind: "CODE", vocabulary: "reminderState", token: "COMPLETED" },
            },
          ],
          unchanged: [{ key: "action", labelId: "field.reminderAction.label" }],
        },
      );
      expect(isResponse("operation.preview", reply)).toBe(true);
      return route.fulfill({ json: reply });
    });
    page.on("request", (request) => {
      if (request.url().endsWith("/api/operation.execute")) {
        writes += 1;
      }
    });
    await page.goto(casePath(current.caseId), { waitUntil: "commit" });
    await expect(page.getByText(rows[0]!.reminder.action, { exact: true })).toBeVisible();
    await expect(page.locator(".case-reminders")).toBeVisible();
    await expect(page.locator(".case-timeline-panel")).toHaveAttribute("aria-busy", "false");
    expect(requests.filter((path) => path === "/api/case.get")).toHaveLength(1);
    expect(requests.filter((path) => path === "/api/case.timeline")).toHaveLength(1);
    expect(requests.filter((path) => path === "/api/reminder.list")).toHaveLength(0);
    if (mobile) {
      await expect(page.locator(".workspace-mobile-tabs")).toBeVisible();
      const last = (await showCaseRecordDetails(page)).getByRole("button").last();
      await last.focus();
      await writeFile(
        testInfo.outputPath("focus-layout.json"),
        JSON.stringify(
          {
            focused: await last.boundingBox(),
            navigation: await page.locator(".workspace-mobile-tabs").boundingBox(),
            document: await page.evaluate(() => ({
              height: innerHeight,
              scrollY,
              scrollPadding: getComputedStyle(document.documentElement).scrollPaddingBottom,
            })),
          },
          null,
          2,
        ),
      );
      await expect
        .poll(async () => {
          const focused = await last.boundingBox();
          const bar = await page.locator(".workspace-mobile-tabs").boundingBox();
          return (
            focused !== null &&
            bar !== null &&
            focused.y >= 0 &&
            focused.y + focused.height <= bar.y
          );
        })
        .toBe(true);
    }
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
    ).toBe(true);
    await expectAccessible(page);
    await clean();
    if (browserName === "chromium") {
      await captureCheckpoint(
        page,
        testInfo.outputPath(`populated-case-${language}-${accent}-${mode}.png`),
      );
    }
    const trigger = page.locator(
      `[id="reminder-${rows[0]!.reminder.reminderId}-REMINDER:REMINDER_COMPLETE"]`,
    );
    await trigger.scrollIntoViewIfNeeded();
    await page.evaluate(
      () =>
        new Promise<void>((resolve) => {
          requestAnimationFrame(() => {
            requestAnimationFrame(() => {
              resolve();
            });
          });
        }),
    );
    await trigger.click();
    const dialog = page.getByRole("dialog");
    const confirm = dialog.locator("[data-confirm-operation]");
    await expect(confirm).toBeEnabled();
    await expect(page.locator(".ui-completion-popover")).toBeVisible();
    await expect(dialog).toContainText(rows[0]!.reminder.action);
    await expect(page.locator(".ui-completion-popover")).toHaveCSS("opacity", "1");
    const geometry = await dialog.evaluate((element) => ({
      dialog: {
        top: element.getBoundingClientRect().top,
        bottom: element.getBoundingClientRect().bottom,
      },
      height: window.innerHeight,
      width: window.innerWidth,
    }));
    await writeFile(
      testInfo.outputPath("completion-geometry.json"),
      JSON.stringify(geometry, null, 2),
    );
    expect(geometry.dialog.top).toBeGreaterThanOrEqual(0);
    expect(geometry.dialog.bottom).toBeLessThanOrEqual(geometry.height);
    const confirmBox = await confirm.boundingBox();
    expect(confirmBox).not.toBeNull();
    expect(confirmBox!.y).toBeGreaterThanOrEqual(geometry.dialog.top);
    expect(confirmBox!.y + confirmBox!.height).toBeLessThanOrEqual(geometry.dialog.bottom);

    expect(previews).toBe(1);
    expect(writes).toBe(0);
    expect(requests.filter((path) => path === "/api/case.get")).toHaveLength(1);
    await expectAccessible(page);
    await clean();
    if (browserName === "chromium") {
      await captureCheckpoint(
        page,
        testInfo.outputPath(`completion-${language}-${accent}-${mode}.png`),
        false,
      );
    }
    await dialog.getByRole("button", { name: ui(language, "ui.close"), exact: true }).click();
    await expect(page.getByRole("dialog")).toBeVisible();
    await page
      .getByRole("button", { name: ui(language, "ui.discardAndLeave"), exact: true })
      .click();
    await expect(page.getByRole("dialog")).toHaveCount(0);
    await expect(trigger).toBeFocused();
    expect(writes).toBe(0);
    await clean();
  });
}
