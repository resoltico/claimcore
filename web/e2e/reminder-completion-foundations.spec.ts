import {
  installationActions,
  requesterLinks,
  activeRequester,
} from "../tests/support/human-requester.fixtures";
import type { OperationTaskDefinition, RequestByEndpoint, Region } from "@contracts/client";
import { caseSubject, caseDocumentInput } from "../tests/support/case-document.fixtures";
import { operationAffordance } from "../tests/support/affordances.fixtures";

import { humanReviewEnvelope } from "../tests/support/human-review-response.fixtures";
import { showCaseRecordDetails } from "./workspace-navigation";
import { humanReadReply } from "../tests/support/human-read-reply.fixtures";
import { writeFile } from "node:fs/promises";
import { expect, test } from "@playwright/test";
import { captureCheckpoint, observe, seed } from "./presentation-probe";
import { expectAccessible } from "./session-helpers";
import { casePath } from "../src/routing/routes";
import { ui } from "./localization-support";

import { timelineDocument } from "../tests/support/timeline-document.fixtures";
const basis = {
  observedAt: "2026-11-04T12:00:00.000000Z",
  businessDate: "2026-11-04",
  timeZone: "Etc/UTC",
};
const reminders: ReadonlyArray<{
  reminderId: string;
  caseId: string;
  revision: number;
  tasks: ReadonlyArray<Extract<OperationTaskDefinition, { subject: "REMINDER" }>["id"]>;
  actionSeed: string;
  dueDateSeed: string;
}> = [
  {
    reminderId: "30000000-0000-4000-8000-000000000003",
    caseId: "20000000-0000-4000-8000-000000000001",
    revision: 3,
    tasks: [
      "reminder.complete",
      "reminder.update",
      "reminder.cancel",
      "reminder.completeAndPlanNext",
    ],
    actionSeed: "Call claimant about invoice",
    dueDateSeed: "2026-10-28",
  },
  {
    reminderId: "30000000-0000-4000-8000-000000000004",
    caseId: "20000000-0000-4000-8000-000000000001",
    revision: 1,
    tasks: [
      "reminder.complete",
      "reminder.update",
      "reminder.cancel",
      "reminder.completeAndPlanNext",
    ],
    actionSeed: "Chase insurer for approval",
    dueDateSeed: "2026-11-04",
  },
];
const reminderRegion: Region = {
  role: "RESULTS",
  content: {
    kind: "TABLE",
    columns: [
      {
        key: "action",
        labelId: "field.reminderAction.label",
        prominence: "PRIMARY",
        isMoney: false,
      },
      {
        key: "due",
        labelId: "field.reminderDueDate.label",
        prominence: "PRIMARY",
        isMoney: false,
      },
      {
        key: "timing",
        labelId: "input.reminderDue.label",
        prominence: "PRIMARY",
        isMoney: false,
      },
    ],
    rows: [
      {
        key: "30000000-0000-4000-8000-000000000003",
        actions: reminders[0]!.tasks.map((task) =>
          operationAffordance(
            task,
            {
              kind: "REMINDER",
              target: { kind: "ID", caseId: reminders[0]!.caseId },
              reminderId: reminders[0]!.reminderId,
              expectedRevision: 3,
            },
            {
              "input:reminderAction": reminders[0]!.actionSeed,
              "input:reminderDueDate": reminders[0]!.dueDateSeed,
            },
          ),
        ),
        values: [
          {
            kind: "TEXT",
            value: "Call claimant about invoice",
          },
          {
            kind: "DATE",
            value: "2026-10-28",
          },
          {
            kind: "CODE",
            vocabulary: "reminderDue",
            token: "OVERDUE",
          },
        ],
      },
      {
        key: "30000000-0000-4000-8000-000000000004",
        actions: reminders[1]!.tasks.map((task) =>
          operationAffordance(
            task,
            {
              kind: "REMINDER",
              target: { kind: "ID", caseId: reminders[1]!.caseId },
              reminderId: reminders[1]!.reminderId,
              expectedRevision: 1,
            },
            {
              "input:reminderAction": reminders[1]!.actionSeed,
              "input:reminderDueDate": reminders[1]!.dueDateSeed,
            },
          ),
        ),
        values: [
          {
            kind: "TEXT",
            value: "Chase insurer for approval",
          },
          {
            kind: "DATE",
            value: "2026-11-04",
          },
          {
            kind: "CODE",
            vocabulary: "reminderDue",
            token: "TODAY",
          },
        ],
      },
    ],
  },
};
const current = {
  ...caseDocumentInput(
    {
      subject: { ...caseSubject, revision: 3 },
      actions: [
        operationAffordance(
          "case.close",
          {
            kind: "CASE",
            target: { kind: "ID", caseId: "20000000-0000-4000-8000-000000000001" },
            expectedRevision: 3,
          },
          {},
          ["field:handlingSummary", "field:responsibleActor"],
          {},
        ),
        operationAffordance(
          "case.void",
          {
            kind: "CASE",
            target: { kind: "ID", caseId: "20000000-0000-4000-8000-000000000001" },
            expectedRevision: 3,
          },
          {},
          ["field:handlingSummary", "field:responsibleActor"],
          {},
        ),
        operationAffordance(
          "case.erase",
          {
            kind: "CASE",
            target: { kind: "ID", caseId: "20000000-0000-4000-8000-000000000001" },
            expectedRevision: 3,
          },
          {},
          ["field:handlingSummary", "field:responsibleActor"],
          {},
        ),
        operationAffordance(
          "work.update",
          {
            kind: "WORK",
            target: { kind: "ID", caseId: "20000000-0000-4000-8000-000000000001" },
            expectedRevision: 3,
          },
          {},
          ["field:handlingSummary", "field:responsibleActor"],
          {},
        ),
        operationAffordance(
          "reminder.create",
          {
            kind: "REMINDER",
            target: { kind: "ID", caseId: "20000000-0000-4000-8000-000000000001" },
          },
          {},
          ["field:handlingSummary", "field:responsibleActor"],
          {},
        ),
      ],
      history: "SUMMARY",
    },
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
                value: {
                  kind: "REFERENCE",
                  value: "CASE-1",
                },
              },
              {
                key: "claimantName",
                labelId: "field.claimantName.label",
                prominence: "PRIMARY",
                value: {
                  kind: "PARTY",
                  value: "شركة الخليج للتجارة",
                },
              },
              {
                key: "headline",
                labelId: "label.caseState",
                prominence: "PRIMARY",
                value: {
                  kind: "STATE",
                  vocabulary: "headline",
                  token: "PAID",
                },
              },
            ],
          },
        },
        {
          role: "PROGRESS",
          content: {
            kind: "STAGES",
            current: "PAID",
            steps: [
              {
                stage: "OPENED",
                reached: true,
                date: "2026-09-02",
                amount: {
                  amount: "25000",
                  currency: "AED",
                  displayScale: 2,
                },
              },
              {
                stage: "DECIDED",
                reached: true,
                date: "2026-10-10",
                amount: {
                  amount: "20000",
                  currency: "AED",
                  displayScale: 2,
                },
              },
              {
                stage: "PAID",
                reached: true,
                date: "2026-10-12",
                amount: null,
              },
              {
                stage: "CLOSED",
                reached: false,
                date: null,
                amount: null,
              },
            ],
          },
        },
        ...[],
        {
          role: "FACTS",
          titleId: "group.registration.label",
          content: {
            kind: "ENTRIES",
            entries: [
              {
                key: "incident",
                labelId: "field.incidentDescription.label",
                prominence: "PRIMARY",
                value: {
                  kind: "NARRATIVE",
                  value: "Collision in the car park. No injuries.",
                },
              },
            ],
          },
        },
        {
          role: "PAYMENTS",
          titleId: "label.payments",
          content: {
            kind: "TABLE",
            columns: [
              {
                key: "date",
                labelId: "field.paymentDate.label",
                prominence: "PRIMARY",
                isMoney: false,
              },
              {
                key: "amount",
                labelId: "field.paidAmount.label",
                prominence: "PRIMARY",
                isMoney: true,
              },
            ],
            rows: [
              {
                key: "10000000-0000-7000-8000-000000000003",
                values: [
                  {
                    kind: "DATE",
                    value: "2026-10-12",
                  },
                  {
                    kind: "MONEY",
                    amount: "20000",
                    currency: "AED",
                    displayScale: 2,
                  },
                ],
              },
            ],
          },
        },
        {
          role: "WORK",
          titleId: "section.work.label",
          content: {
            kind: "ENTRIES",
            entries: [
              {
                key: "summary",
                labelId: "field.handlingSummary.label",
                prominence: "PRIMARY",
                value: {
                  kind: "NARRATIVE",
                  value: "Waiting for a repair invoice from the claimant.",
                },
              },
            ],
          },
        },
        {
          ...reminderRegion,
          role: "NEXT_ACTIONS",
        },
        {
          role: "DETAILS",
          titleId: "label.recordDetails",
          content: {
            kind: "ENTRIES",
            entries: [
              {
                key: "caseId",
                labelId: "input.caseId.label",
                prominence: "DETAIL",
                value: {
                  kind: "IDENTIFIER",
                  value: "20000000-0000-4000-8000-000000000001",
                },
              },
            ],
          },
        },
      ],
    },
  ),
  basis: {
    kind: "SNAPSHOT" as const,
    observation: basis,
  },
};
const contexts = [
  {
    language: "en",
    accent: "green",
    mode: "light",
    mobile: false,
  },
  {
    language: "en",
    accent: "green",
    mode: "dark",
    mobile: false,
  },
  {
    language: "ar",
    accent: "blue",
    mode: "dark",
    mobile: true,
  },
] as const;
for (const { language, accent, mode, mobile } of contexts) {
  test(`populated reminder completion and focus safety in ${language} ${accent} ${mode}`, async ({
    page,
    browserName,
  }, testInfo) => {
    const clean = await observe(page, true);
    await seed(page, {
      language,
      accent,
      mode,
    });
    await page.setViewportSize(
      mobile
        ? {
            width: 390,
            height: 844,
          }
        : {
            width: 1440,
            height: 1000,
          },
    );
    await page.route("**/api/context", (route) =>
      route.fulfill({
        json: {
          ...humanReadReply("context", {
            ...activeRequester,
            actions: installationActions([]),
            links: requesterLinks(["CASES", "OPERATIONS"], []),
            basis: {
              kind: "SNAPSHOT",
              observation: basis,
            },
          }),
        },
      }),
    );
    await page.route("**/api/case.get", (route) =>
      route.fulfill({
        json: {
          ...humanReadReply("case.get", current),
        },
      }),
    );
    await page.route("**/api/case.timeline", (route) =>
      route.fulfill({
        json: {
          ...humanReadReply("case.timeline", {
            ...timelineDocument([], null, "SUMMARY"),
            basis: {
              kind: "SNAPSHOT",
              observation: basis,
            },
          }),
        },
      }),
    );
    await page.route("**/api/reminder.list", (route) =>
      route.fulfill({
        json: {
          ...humanReadReply("reminder.list", {
            screen: {
              regions: [reminderRegion],
            },
            subject: { kind: "LIST" },
            actions: [],
            links: [],
            history: null,
            nextCursor: null,
            basis: {
              kind: "SNAPSHOT",
              observation: basis,
            },
            disclosure: "CURRENT",
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
      const operationPreviewRequest = route
        .request()
        .postDataJSON() as RequestByEndpoint["operation.preview"];
      if (
        operationPreviewRequest.binding.kind !== "REMINDER" ||
        operationPreviewRequest.task !== "reminder.complete"
      ) {
        throw new Error("Expected one frozen reminder completion preview.");
      }
      expect(operationPreviewRequest.binding).toEqual({
        kind: "REMINDER",
        target: operationPreviewRequest.binding.target,
        reminderId: reminders[0]!.reminderId,
        expectedRevision: 3,
      });
      previews += 1;
      const reply = humanReviewEnvelope(
        operationPreviewRequest,
        {
          target: {
            kind: "REMINDER",
            caseId: current.subject.caseId,
            existing: {
              reminderId: operationPreviewRequest.binding.reminderId,
              revision: 3,
            },
          },
          confirmation: {
            kind: "CASE_REFERENCE",
            reference: "CASE-1",
          },
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
                    value: {
                      kind: "REFERENCE",
                      value: "CASE-1",
                    },
                  },
                  {
                    key: "reminderAction",
                    labelId: "field.reminderAction.label",
                    prominence: "PRIMARY",
                    value: {
                      kind: "TEXT",
                      value: "Call claimant about invoice",
                    },
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
              before: {
                kind: "CODE",
                vocabulary: "reminderState",
                token: "PENDING",
              },
              after: {
                kind: "CODE",
                vocabulary: "reminderState",
                token: "COMPLETED",
              },
            },
          ],
          unchanged: [
            {
              key: "action",
              labelId: "field.reminderAction.label",
            },
          ],
        },
      );
      return route.fulfill({
        json: reply,
      });
    });
    page.on("request", (request) => {
      if (request.url().endsWith("/api/operation.execute")) {
        writes += 1;
      }
    });
    await page.goto(casePath(current.subject.caseId), {
      waitUntil: "commit",
    });
    await expect(
      page.getByText("Call claimant about invoice", {
        exact: true,
      }),
    ).toBeVisible();
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
    const trigger = page.locator(`[id="reminder-${reminders[0]!.reminderId}-reminder.complete"]`);
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
    await expect(dialog).toContainText("Call claimant about invoice");
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
    await dialog
      .getByRole("button", {
        name: ui(language, "ui.close"),
        exact: true,
      })
      .click();
    await expect(page.getByRole("dialog")).toBeVisible();
    await page
      .getByRole("button", {
        name: ui(language, "ui.discardAndLeave"),
        exact: true,
      })
      .click();
    await expect(page.getByRole("dialog")).toHaveCount(0);
    await expect(trigger).toBeFocused();
    expect(writes).toBe(0);
    await clean();
  });
}
