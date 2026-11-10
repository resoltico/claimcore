import { humanReadReply } from "../support/human-read-reply.fixtures";
import { it, expect, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { ReminderQueueWork } from "../../src/views/work/ReminderQueueWork";
import { mockEndpoint } from "../support/endpoint-mocks";
import { service } from "../../src/api/service";
import type { HumanReceiptDocument } from "@contracts/client";
import { current } from "../support/presentation-state.fixtures";
import { workBasis } from "../support/inputs";
import { fields } from "../support/service-ui.fixtures";
import { reviewConfirm, waitForReviewConfirm } from "../support/review-controls";
const reminderId = "30000000-0000-4000-8000-000000000003";
const receipt: HumanReceiptDocument = {
  screen: {
    regions: [
      {
        role: "NOTICE",
        content: {
          kind: "ENTRIES",
          entries: [
            {
              key: "result",
              labelId: "label.details",
              prominence: "PRIMARY",
              value: { kind: "MESSAGE", messageId: "act.REMINDER_COMPLETE.done" },
            },
          ],
        },
      },
    ],
  },
  context: {
    kind: "RECEIPT",
    target: {
      kind: "REMINDER",
      caseId: current.context.caseId,
      versions: [{ reminderId, revision: 4 }],
    },
    refreshCase: current.context.caseId,
  },
  basis: { kind: "SNAPSHOT", observation: workBasis },
  disclosure: "CURRENT",
};
const setup = () => {
  const list = mockEndpoint("reminder.list")
    .mockResolvedValueOnce({
      kind: "outcome",
      status: 200,
      value: humanReadReply("reminder.list", {
        screen: {
          regions: [
            {
              role: "RESULTS",
              content: {
                kind: "TABLE",
                columns: [
                  {
                    key: "caseReference",
                    labelId: "field.caseReference.label",
                    prominence: "PRIMARY",
                    isMoney: false,
                  },
                  {
                    key: "action",
                    labelId: "input.reminderAction.label",
                    prominence: "PRIMARY",
                    isMoney: false,
                  },
                ],
                rows: [
                  {
                    key: reminderId,
                    values: [
                      { kind: "REFERENCE", value: "CASE-1" },
                      { kind: "TEXT", value: "Call the claimant" },
                    ],
                  },
                ],
              },
            },
          ],
        },
        context: {
          kind: "REMINDERS",
          reminders: [
            {
              reminderId,
              caseId: current.context.caseId,
              revision: 3,
              tasks: [
                "REMINDER:REMINDER_COMPLETE",
                "REMINDER:REMINDER_UPDATE",
                "REMINDER:REMINDER_CANCEL",
                "REMINDER:REMINDER_COMPLETE_AND_PLAN_NEXT",
              ],
              actionSeed: "Call the claimant",
              dueDateSeed: "2026-11-01",
            },
          ],
          nextCursor: null,
        },
        basis: { kind: "SNAPSHOT", observation: workBasis },
        disclosure: "CURRENT",
      }),
    })
    .mockResolvedValue({
      kind: "outcome",
      status: 200,
      value: humanReadReply("reminder.list", {
        screen: { regions: [] },
        context: { kind: "REMINDERS", reminders: [], nextCursor: null },
        basis: { kind: "SNAPSHOT", observation: workBasis },
        disclosure: "CURRENT",
      }),
    });
  const read = mockEndpoint("case.get").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: { representation: "HUMAN", outcome: { kind: "SUCCEEDED", document: current } },
  });
  const preview = vi.spyOn(service, "preview").mockImplementation((prepared) =>
    Promise.resolve({
      kind: "outcome",
      status: 200,
      value: {
        representation: "HUMAN",
        operationId: prepared.input.operationId,
        outcome: {
          kind: "REVIEWED",
          review: {
            document: {
              screen: { regions: [] },
              context: {
                kind: "REVIEW",
                task: "REMINDER:REMINDER_COMPLETE",
                inputs: [
                  "input:operationId",
                  "field:caseReference",
                  "input:reminderId",
                  "input:expectedReminderRevision",
                ],
                consequence: "ROUTINE",
                target: {
                  kind: "REMINDER",
                  caseId: current.context.caseId,
                  existing: { reminderId, revision: 3 },
                },
                confirmation: { kind: "CASE_REFERENCE", reference: fields.caseReference },
              },
              basis: { kind: "SNAPSHOT", observation: workBasis },
              disclosure: "CURRENT",
            },
            presentation: {
              changes: [
                {
                  key: "state",
                  labelId: "input.reminderState.label",
                  before: { kind: "CODE", vocabulary: "reminderState", token: "PENDING" },
                  after: { kind: "CODE", vocabulary: "reminderState", token: "COMPLETED" },
                },
              ],
              unchanged: [],
            },
          },
        },
      },
    }),
  );
  const execute = vi.spyOn(service, "execute").mockImplementation((prepared) =>
    Promise.resolve({
      kind: "outcome",
      status: 200,
      value: {
        representation: "HUMAN",
        operationId: prepared.input.operationId,
        outcome: { kind: "ACCEPTED", document: receipt },
      },
    }),
  );
  mockEndpoint("operation.get").mockImplementation((_credential, input) =>
    Promise.resolve({
      kind: "outcome",
      status: 200,
      value: {
        representation: "HUMAN",
        operationId: input.operationId,
        outcome: { kind: "ACCEPTED", byRequester: true, document: receipt },
      },
    }),
  );
  const lock = vi.fn();
  render(
    <ReminderQueueWork filter={{ view: "PENDING" }} onLockChange={lock} disclosureAvailable />,
  );
  return { list, read, preview, execute, lock };
};
it("opens a compact completion preview from an explicit row action and writes only after confirmation", async () => {
  const { read, preview, execute } = setup();
  const user = userEvent.setup();
  const complete = await screen.findByRole("button", { name: "Complete reminder" });
  expect(read).not.toHaveBeenCalled();
  expect(preview).not.toHaveBeenCalled();
  expect(execute).not.toHaveBeenCalled();
  await user.click(complete);
  await waitForReviewConfirm();
  expect(document.querySelector(".ui-completion-popover")).not.toBeNull();
  expect(read).toHaveBeenCalledTimes(1);
  expect(preview).toHaveBeenCalledTimes(1);
  expect(execute).not.toHaveBeenCalled();
  expect(preview.mock.calls[0]![0].input).toMatchObject({
    kind: "REMINDER",
    target: { kind: "ID", caseId: current.context.caseId },
    act: { kind: "COMPLETE", reminderId, expectedRevision: 3 },
  });
  await user.click(reviewConfirm());
  await waitFor(() => {
    expect(execute).toHaveBeenCalledTimes(1);
  });
  expect(execute.mock.calls[0]![0]).toBe(preview.mock.calls[0]![0]);
  await waitFor(() => {
    expect(screen.queryByRole("dialog")).toBeNull();
  });
  expect(await screen.findByText("Reminder completed.")).toBeVisible();
  expect(screen.queryByText("Call the claimant")).toBeNull();
});

it("keeps the selected frozen completion after a refusal and masks queue facts through later unknown replies", async () => {
  const { read, preview, execute } = setup();
  execute
    .mockReset()
    .mockResolvedValueOnce({
      kind: "deliveryFailure",
      notice: { kind: "local", reason: "unreachable" },
      dispatched: true,
    })
    .mockImplementationOnce((prepared) =>
      Promise.resolve({
        kind: "outcome",
        status: 200,
        value: {
          representation: "HUMAN",
          operationId: prepared.input.operationId,
          outcome: {
            kind: "REJECTED",
            rejection: {
              kind: "REFUSED",
              advice: {
                message: { messageId: "diagnostic.RESOURCE_UNAVAILABLE", arguments: [] },
                action: "ASK_FOR_ACCESS",
              },
            },
          },
        },
      }),
    )
    .mockImplementation((prepared) =>
      Promise.resolve({
        kind: "outcome",
        status: 200,
        value: {
          representation: "HUMAN",
          operationId: prepared.input.operationId,
          outcome: { kind: "OUTCOME_UNKNOWN" },
        },
      }),
    );
  const user = userEvent.setup();
  await user.click(await screen.findByRole("button", { name: "Complete reminder" }));
  await waitForReviewConfirm();
  await user.click(reviewConfirm());
  await user.click(await screen.findByRole("button", { name: "Send again" }));
  await waitFor(() => {
    expect(document.body).not.toHaveTextContent("Call the claimant");
  });
  expect(document.querySelector(".ui-completion-popover")).toBeNull();
  expect(screen.getByRole("dialog")).toBeVisible();
  await user.click(screen.getByRole("button", { name: "Send again" }));
  await waitFor(() => {
    expect(execute).toHaveBeenCalledTimes(3);
  });
  expect(document.body).not.toHaveTextContent("Call the claimant");
  expect(read).toHaveBeenCalledTimes(1);
  expect(preview).toHaveBeenCalledTimes(1);
  expect(execute.mock.calls[1]![0]).toBe(execute.mock.calls[0]![0]);
  expect(execute.mock.calls[2]![0]).toBe(execute.mock.calls[0]![0]);
});
