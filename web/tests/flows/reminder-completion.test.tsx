import { it, expect, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { ReminderQueueWork } from "../../src/views/work/ReminderQueueWork";
import { mockEndpoint } from "../support/endpoint-mocks";
import { service, type ServiceResponse } from "../../src/api/service";
import type { ReminderReadModel } from "@contracts/client";
import { current } from "../support/presentation-state.fixtures";
import { workBasis, actorRefInput } from "../support/inputs";
import { runtime } from "../support/service-ui.fixtures";
import { reviewConfirm, waitForReviewConfirm } from "../support/review-controls";
const row: ReminderReadModel = {
  reminder: {
    reminderId: "30000000-0000-4000-8000-000000000003",
    caseId: current.caseId,
    revision: 3,
    action: "Call the claimant",
    dueDate: "2026-11-01",
    state: "PENDING",
  },
  caseReference: current.caseReference,
  recordedAt: workBasis.observedAt,
  recordedBy: actorRefInput(),
  acts: ["COMPLETE", "UPDATE", "CANCEL", "COMPLETE_AND_PLAN_NEXT"],
  active: true,
  timing: "OVERDUE",
};
const completed = { ...row.reminder, revision: 4, state: "COMPLETED" as const };
const setup = () => {
  const list = mockEndpoint("reminder.list").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      outcome: { kind: "SUCCEEDED", data: { basis: workBasis, items: [row], nextCursor: null } },
    },
  });
  const read = mockEndpoint("case.get").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: { outcome: { kind: "SUCCEEDED", data: current } },
  });
  const preview = vi.spyOn(service, "preview").mockImplementation((prepared) =>
    Promise.resolve({
      kind: "outcome",
      status: 200,
      value: {
        operationId: prepared.input.operationId,
        outcome: {
          kind: "REVIEWED",
          data: {
            review: {
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
              subject: {
                kind: "REMINDER",
                caseId: current.caseId,
                caseReference: current.caseReference,
                parent: {
                  caseId: current.caseId,
                  caseReference: current.caseReference,
                  revision: current.revision,
                  state: current.state,
                },
                before: row.reminder,
                effects: { kind: "CHANGED", reminder: completed },
                context: runtime,
              },
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
        operationId: prepared.input.operationId,
        outcome: {
          kind: "ACCEPTED",
          data: {
            operationId: prepared.input.operationId,
            position: { sequence: 4, entryHash: "a".repeat(64) },
            recordedAt: workBasis.observedAt,
            recordedBy: actorRefInput(),
            basis: workBasis,
            subject: {
              kind: "REMINDER",
              caseId: current.caseId,
              caseReference: current.caseReference,
              act: "COMPLETE",
              effects: {
                kind: "SINGLE",
                reminder: {
                  reminderId: completed.reminderId,
                  revision: 4,
                  snapshot: { kind: "DISCLOSED", view: completed },
                },
              },
            },
          },
        },
      } satisfies ServiceResponse<"operation.execute">,
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
    target: { kind: "REFERENCE", caseReference: current.caseReference },
    act: { kind: "COMPLETE", reminderId: row.reminder.reminderId, expectedRevision: 3 },
  });
  await user.click(reviewConfirm());
  await waitFor(() => {
    expect(execute).toHaveBeenCalledTimes(1);
  });
  expect(execute.mock.calls[0]![0]).toBe(preview.mock.calls[0]![0]);
  await waitFor(() => {
    expect(screen.queryByRole("dialog")).toBeNull();
  });
  expect(screen.getByRole("status")).toHaveTextContent("Reminder completed");
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
          operationId: prepared.input.operationId,
          outcome: {
            kind: "REJECTED",
            data: { code: "RESOURCE_UNAVAILABLE", action: "ASK_FOR_ACCESS" },
          },
        },
      }),
    )
    .mockImplementation((prepared) =>
      Promise.resolve({
        kind: "outcome",
        status: 200,
        value: {
          operationId: prepared.input.operationId,
          outcome: { kind: "OUTCOME_UNKNOWN", data: null },
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
