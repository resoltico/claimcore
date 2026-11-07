import { expect, it } from "vitest";
import { readConflict } from "../../../../src/hooks/operation/readConflict";
import { mockEndpoint } from "../../../support/endpoint-mocks";
import { caseViewInput, actorRefInput } from "../../../support/inputs";
import { fields } from "../../../support/service-ui.fixtures";
import { caseAdvice } from "../../../support/case-advice.fixtures";
import { prepareRequest } from "../../../../src/protocol/preparedRequest";
import type { OperationInput } from "@contracts/client";
import type { ConflictSource, OperationEffect } from "../../../../src/operation/operationTypes";
const succeeded = <const T>(data: T) => ({
  kind: "outcome" as const,
  status: 200,
  value: { outcome: { kind: "SUCCEEDED" as const, data } },
});
const caseValue = () =>
  caseViewInput({
    caseReference: "CASE-1",
    revision: 100,
    state: { kind: "ACTIVE", fields },
    advice: { ...caseAdvice([]), history: "NONE" },
    work: {
      changedAt: null,
      recordedBy: null,
      revision: 7,
      summary: null,
      summaryChangedAt: null,
      summaryRecordedBy: null,
      nextAction: null,
      pendingReminders: 0,
      overdueReminders: 0,
      needsNextAction: false,
      canUpdateSummary: true,
      canAssign: false,
      canCreateReminder: false,
      responsible: null,
      followUpDate: null,
      due: "UNSCHEDULED",
      basis: {
        businessDate: "2026-11-04",
        timeZone: "Etc/UTC",
        observedAt: "2026-11-04T12:00:00.000000Z",
      },
    },
  });
const scoped = (source: {
  prepared: ReturnType<typeof prepareRequest<"operation.execute">>;
}): source is ConflictSource => source.prepared.input.kind !== "AUTHORITY";
const effectFor = (input: OperationInput): Extract<OperationEffect, { kind: "READ_CONFLICT" }> => {
  const source = { prepared: prepareRequest("operation.execute", input) };
  if (!scoped(source)) {
    throw new Error("Expected a case-bound operation.");
  }
  const identity = {
    id: "read-conflict",
    contextGeneration: 0,
    credential: "csrf",
    target: "bound-case",
  };
  return {
    kind: "READ_CONFLICT",
    identity,
    snapshot: {
      identity,
      source,
      diagnostic: {
        code: "REVISION_CONFLICT",
        action: "REVIEW_LATEST",
        actualRevision: 5,
        changes: { kind: "WITHHELD" },
      },
      latest: null,
    },
  };
};
const root = {
  operationId: "10000000-0000-4000-8000-000000000001",
  target: { kind: "REFERENCE" as const, caseReference: "CASE-1" },
};
it("uses the current work revision separately from the case fact revision", async () => {
  mockEndpoint("case.get").mockResolvedValue(succeeded(caseValue()));
  const reminder = mockEndpoint("reminder.get");
  const reads = new Map<string, AbortController>();
  const result = await readConflict(
    effectFor({
      kind: "WORK",
      ...root,
      expectedRevision: 1,
      act: { kind: "UPDATE_WORK", values: { handlingSummary: "Retained summary" } },
    }),
    reads,
  );
  expect(result.type).toBe("CONFLICT_OBSERVED");
  if (result.type !== "CONFLICT_OBSERVED") {
    throw new Error("Expected refreshed work.");
  }
  expect(result.value.revision).toBe(7);
  expect(result.value.current.revision).toBe(100);
  expect(reminder).not.toHaveBeenCalled();
  expect(reads.size).toBe(0);
});
it("reads the selected reminder revision rather than either case or work revision", async () => {
  mockEndpoint("case.get").mockResolvedValue(succeeded(caseValue()));
  const reminderId = "30000000-0000-4000-8000-000000000003";
  const get = mockEndpoint("reminder.get").mockResolvedValue(
    succeeded({
      basis: caseValue().work.basis,
      item: {
        reminder: {
          reminderId,
          caseId: caseValue().caseId,
          revision: 9,
          action: "Current action",
          dueDate: "2026-11-05",
          state: "PENDING",
        },
        caseReference: "CASE-1",
        recordedAt: "2026-11-04T12:00:00.000000Z",
        recordedBy: actorRefInput(),
        active: true,
        timing: "UPCOMING",
        acts: ["UPDATE"],
      },
    }),
  );
  const result = await readConflict(
    effectFor({
      kind: "REMINDER",
      ...root,
      act: {
        kind: "UPDATE",
        reminderId,
        expectedRevision: 1,
        action: "Retained action",
        dueDate: "2026-11-06",
      },
    }),
    new Map(),
  );
  expect(result.type).toBe("CONFLICT_OBSERVED");
  if (result.type !== "CONFLICT_OBSERVED") {
    throw new Error("Expected refreshed reminder.");
  }
  expect(result.value.revision).toBe(9);
  expect(get.mock.calls[0]?.[1]).toEqual({
    target: { kind: "REFERENCE", caseReference: "CASE-1" },
    reminderId,
  });
});
it("retains an erased current-case status without inventing editable facts or a reminder revision", async () => {
  mockEndpoint("case.get").mockResolvedValue(
    succeeded(
      caseViewInput({
        caseReference: "CASE-1",
        revision: 101,
        state: { kind: "ERASED" },
        advice: { ...caseAdvice([]), history: "SUMMARY" },
      }),
    ),
  );
  const get = mockEndpoint("reminder.get");
  const result = await readConflict(
    effectFor({
      kind: "REMINDER",
      ...root,
      act: {
        kind: "CANCEL",
        reminderId: "30000000-0000-4000-8000-000000000003",
        expectedRevision: 1,
      },
    }),
    new Map(),
  );
  expect(result.type).toBe("CONFLICT_OBSERVED");
  if (result.type !== "CONFLICT_OBSERVED") {
    throw new Error("Expected the erased status.");
  }
  expect(result.value.revision).toBeNull();
  expect(result.value.current.state.kind).toBe("ERASED");
  expect(get).not.toHaveBeenCalled();
});
