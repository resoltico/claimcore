import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor } from "../../support/presentation-test-support";
import { CaseReassignment } from "../../../src/views/worklist/CaseReassignment";
import {
  isRequest,
  type HumanReadDocument_caseGet,
  type HumanReview,
  type HumanReceiptDocument,
} from "@contracts/client";

const caseId = "00000000-0000-4000-8000-000000000001";
const actorId = "00000000-0000-4000-8000-000000000002";
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
    revision: 7,
    workRevision: 12,
    history: "NONE",
    tasks: ["WORK:UPDATE_WORK"],
    editableInputs: ["field:responsibleActor"],
    correction: null,
    seeds: {
      registration: null,
      decision: null,
      work: { responsible: actorId, summary: null },
      paymentCurrency: null,
    },
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
      task: "WORK:UPDATE_WORK",
      target: { kind: "WORK", caseId, beforeRevision: 12 },
      inputs: [
        "input:operationId",
        "field:caseReference",
        "input:expectedWorkRevision",
        "field:responsibleActor",
        "field:handlingSummary",
        "input:responsibleActorAction",
      ],
      consequence: "ROUTINE",
      confirmation: { kind: "CASE_REFERENCE", reference: "CORE-CASE" },
    },
    basis,
    disclosure: "CURRENT",
  },
};
const receipt: HumanReceiptDocument = {
  screen: { regions: [] },
  context: { kind: "RECEIPT", target: { kind: "WORK", caseId, revision: 13 }, refreshCase: caseId },
  basis,
  disclosure: "CURRENT",
};
const response = (outcome: unknown, operationId?: string) =>
  new Response(
    JSON.stringify({
      representation: "HUMAN",
      ...(operationId === undefined ? {} : { operationId }),
      outcome,
    }),
    { status: 200, headers: { "Content-Type": "application/json" } },
  );
afterEach(() => vi.unstubAllGlobals());
it("keeps the selected list revision until explicit rebase and requires fresh consent for unchanged assignee input", async () => {
  const previews: Array<{ bytes: string; operationId: string }> = [];
  const executions: string[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn<typeof fetch>().mockImplementation(async (url, options) => {
      if (String(url).endsWith("case.get")) {
        return response({ kind: "SUCCEEDED", document: current });
      }
      if (String(url).endsWith("case.assignees")) {
        return response({
          kind: "SUCCEEDED",
          document: {
            screen: {
              regions: [
                {
                  role: "RESULTS",
                  content: {
                    kind: "TABLE",
                    columns: [
                      {
                        key: "actor",
                        labelId: "label.details",
                        prominence: "PRIMARY",
                        isMoney: false,
                      },
                    ],
                    rows: [{ key: actorId, values: [{ kind: "TEXT", value: "Core assignee Ω" }] }],
                  },
                },
              ],
            },
            context: { kind: "ASSIGNEES", actorIds: [actorId], nextCursor: null },
            basis,
            disclosure: "CURRENT",
          },
        });
      }
      const bytes = String(options?.body);
      const command: unknown = JSON.parse(bytes);
      if (!isRequest("operation.execute", command)) {
        throw new Error("Expected a typed work command");
      }
      if (String(url).endsWith("preview")) {
        previews.push({ bytes, operationId: command.operationId });
        return previews.length === 1
          ? response(
              {
                kind: "REJECTED",
                rejection: {
                  kind: "CONFLICT",
                  actualRevision: 12,
                  advice: {
                    message: { messageId: "ui.conflict.title", arguments: [] },
                    action: "REVIEW_LATEST",
                  },
                  document: {
                    screen: { regions: [] },
                    context: { kind: "EMPTY" },
                    basis: { kind: "UNAVAILABLE" },
                    disclosure: "WITHHELD",
                  },
                },
              },
              command.operationId,
            )
          : response({ kind: "REVIEWED", review }, command.operationId);
      }
      executions.push(bytes);
      return response({ kind: "ACCEPTED", document: receipt }, command.operationId);
    }),
  );
  const recorded = vi.fn();
  render(
    <CaseReassignment
      selection={{
        row: { caseId, assignment: { revision: 3, responsibleSeed: actorId } },
        readId: "selection",
      }}
      disclosureAvailable
      close={vi.fn()}
      recorded={recorded}
      onLockChange={vi.fn()}
      refused={vi.fn()}
    />,
  );
  expect(await screen.findByRole("radio", { name: "Core assignee Ω" })).toBeChecked();
  await userEvent.click(screen.getByRole("button", { name: "Review changes" }));
  const rebase = await screen.findByRole("button", { name: /latest/i });
  await waitFor(() => expect(rebase).toBeEnabled());
  expect(JSON.parse(previews[0]!.bytes)).toMatchObject({
    kind: "WORK",
    target: { kind: "ID", caseId },
    expectedRevision: 3,
    act: { kind: "UPDATE_WORK", values: { responsibleActor: { kind: "SET", value: actorId } } },
  });
  expect(executions).toEqual([]);
  await userEvent.click(rebase);
  await waitFor(() =>
    expect(screen.getByRole("dialog").querySelector("[data-confirm-operation]")).not.toBeNull(),
  );
  expect(previews).toHaveLength(2);
  expect(previews[1]!.operationId).not.toBe(previews[0]!.operationId);
  expect(JSON.parse(previews[1]!.bytes)).toMatchObject({
    kind: "WORK",
    target: { kind: "ID", caseId },
    expectedRevision: 12,
    act: { kind: "UPDATE_WORK", values: { responsibleActor: { kind: "SET", value: actorId } } },
  });
  expect(executions).toEqual([]);
  await userEvent.click(
    screen.getByRole("dialog").querySelector<HTMLButtonElement>("[data-confirm-operation]")!,
  );
  await waitFor(() => expect(recorded).toHaveBeenCalledExactlyOnceWith(receipt));
  expect(executions).toEqual([previews[1]!.bytes]);
});
