import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor, TestSession } from "../../support/presentation-test-support";
import { ReminderQueueWork } from "../../../src/views/work/ReminderQueueWork";
import {
  isRequest,
  type HumanReadDocument_caseGet,
  type HumanReview,
  type HumanReceiptDocument,
} from "@contracts/client";

const caseId = "00000000-0000-4000-8000-000000000001";
const reminderId = "00000000-0000-4000-8000-000000000002";
const basis = {
  kind: "SNAPSHOT",
  observation: {
    observedAt: "2026-11-09T12:00:00.000000Z",
    businessDate: "2026-11-09",
    timeZone: "Etc/UTC",
  },
} as const;
const current: HumanReadDocument_caseGet = {
  screen: { regions: [] },
  context: {
    kind: "CASE",
    caseId,
    revision: 4,
    workRevision: 6,
    history: "NONE",
    tasks: [],
    editableInputs: [],
    correction: null,
    seeds: { paymentCurrency: null, registration: null, decision: null, work: null },
    notes: [],
    payments: [],
    nextActions: [],
  },
  basis,
  disclosure: "CURRENT",
};
const review: HumanReview = {
  presentation: { changes: [], unchanged: [] },
  document: {
    screen: { regions: [] },
    context: {
      kind: "REVIEW",
      task: "REMINDER:REMINDER_COMPLETE",
      target: { kind: "REMINDER", caseId, existing: { reminderId, revision: 9 } },
      inputs: [
        "input:operationId",
        "field:caseReference",
        "input:reminderId",
        "input:expectedReminderRevision",
      ],
      consequence: "ROUTINE",
      confirmation: { kind: "CASE_REFERENCE", reference: "CORE-SELECTED" },
    },
    basis,
    disclosure: "CURRENT",
  },
};
const receipt: HumanReceiptDocument = {
  screen: {
    regions: [
      {
        role: "NOTICE",
        content: {
          kind: "ENTRIES",
          entries: [
            {
              key: "receipt",
              labelId: "label.details",
              prominence: "PRIMARY",
              value: { kind: "TEXT", value: "Core accepted reminder Ψ" },
            },
          ],
        },
      },
    ],
  },
  context: {
    kind: "RECEIPT",
    target: { kind: "REMINDER", caseId, versions: [{ reminderId, revision: 10 }] },
    refreshCase: caseId,
  },
  basis,
  disclosure: "CURRENT",
};
const page = {
  screen: {
    regions: [
      {
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
          ],
          rows: [
            { key: reminderId, values: [{ kind: "TEXT", value: "Core selected reminder Ω" }] },
          ],
        },
      },
    ],
  },
  context: {
    kind: "REMINDERS",
    nextCursor: null,
    reminders: [
      {
        caseId,
        reminderId,
        revision: 9,
        tasks: ["REMINDER:REMINDER_COMPLETE"],
        actionSeed: null,
        dueDateSeed: null,
      },
    ],
  },
  basis,
  disclosure: "CURRENT",
};
afterEach(() => vi.unstubAllGlobals());
it("takes one parent read and carries the selected reminder version through review, consent and the supplied accepted receipt", async () => {
  const sent: string[] = [];
  const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation(async (url, options) => {
    let outcome: unknown;
    let operationId: string | undefined;
    if (String(url).endsWith("reminder.list")) {
      outcome = { kind: "SUCCEEDED", document: page };
    } else if (String(url).endsWith("case.get")) {
      outcome = { kind: "SUCCEEDED", document: current };
    } else {
      const command: unknown = JSON.parse(String(options?.body));
      if (!isRequest("operation.execute", command)) {
        throw new Error("Invalid assembled reminder command");
      }
      operationId = command.operationId;
      sent.push(String(options?.body));
      outcome = String(url).endsWith("preview")
        ? { kind: "REVIEWED", review }
        : { kind: "ACCEPTED", document: receipt };
    }
    return new Response(
      JSON.stringify({
        representation: "HUMAN",
        ...(operationId === undefined ? {} : { operationId }),
        outcome,
      }),
      { status: 200, headers: { "Content-Type": "application/json" } },
    );
  });
  vi.stubGlobal("fetch", fetch);
  const view = render(
    <TestSession credential="owner-a">
      <ReminderQueueWork filter={{}} onLockChange={vi.fn()} disclosureAvailable />
    </TestSession>,
  );
  await screen.findByText("Core selected reminder Ω");
  await userEvent.click(screen.getByRole("button", { name: /complete/i }));
  await waitFor(() =>
    expect(screen.getByRole("dialog").querySelector("[data-confirm-operation]")).not.toBeNull(),
  );
  await userEvent.click(
    screen.getByRole("dialog").querySelector<HTMLButtonElement>("[data-confirm-operation]")!,
  );
  await screen.findByText("Core accepted reminder Ψ");
  expect(sent).toHaveLength(2);
  expect(sent[1]).toBe(sent[0]);
  expect(JSON.parse(sent[0]!)).toMatchObject({
    kind: "REMINDER",
    target: { kind: "ID", caseId },
    act: { kind: "COMPLETE", reminderId, expectedRevision: 9 },
  });
  expect(fetch.mock.calls.filter(([url]) => String(url).endsWith("case.get"))).toHaveLength(1);
  view.rerender(
    <TestSession credential="owner-b">
      <ReminderQueueWork filter={{}} onLockChange={vi.fn()} disclosureAvailable />
    </TestSession>,
  );
  expect(screen.queryByText("Core accepted reminder Ψ")).not.toBeInTheDocument();
  await screen.findByText("Core selected reminder Ω");
  expect(sent).toHaveLength(2);
});
