import { expect, it } from "vitest";
import { readConflict } from "../../../../src/hooks/operation/readConflict";
import { mockEndpoint } from "../../../support/endpoint-mocks";
import { current } from "../../../support/operation-form.fixtures";
import { prepareRequest } from "../../../../src/protocol/preparedRequest";
import type { OperationInput } from "@contracts/client";
import type {
  ConflictSource,
  LocalSource,
  OperationEffect,
} from "../../../../src/operation/operationTypes";
const succeeded = <const T>(data: T) => ({
  kind: "outcome" as const,
  status: 200,
  value: {
    representation: "HUMAN" as const,
    outcome: { kind: "SUCCEEDED" as const, document: data },
  },
});
const caseValue = () => ({
  ...current,
  context: {
    ...current.context,
    revision: 100,
    workRevision: 7,
    history: "NONE" as const,
    tasks: ["WORK:UPDATE_WORK"] as const,
    editableInputs: ["field:handlingSummary"] as const,
    correction: null,
    seeds: {
      registration: null,
      decision: null,
      work: { summary: null, responsible: null },
      paymentCurrency: null,
    },
  },
});
const scoped = (source: LocalSource): source is ConflictSource =>
  source.prepared.input.kind !== "AUTHORITY";
const effectFor = (input: OperationInput): Extract<OperationEffect, { kind: "READ_CONFLICT" }> => {
  const source = { prepared: prepareRequest("operation.execute", input), submittedRows: {} };
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
        kind: "CONFLICT",
        actualRevision: 5,
        advice: {
          message: { messageId: "diagnostic.REVISION_CONFLICT", arguments: [] },
          action: "REVIEW_LATEST",
        },
        document: {
          screen: { regions: [] },
          context: { kind: "PAGE", tasks: [], nextCursor: null },
          basis: current.basis,
          disclosure: "WITHHELD",
        },
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
  expect(result.value.current.context.revision).toBe(100);
  expect(reminder).not.toHaveBeenCalled();
  expect(reads.size).toBe(0);
});
it("reads the selected reminder revision rather than either case or work revision", async () => {
  mockEndpoint("case.get").mockResolvedValue(succeeded(caseValue()));
  const reminderId = "30000000-0000-4000-8000-000000000003";
  const get = mockEndpoint("reminder.get").mockResolvedValue(
    succeeded({
      screen: { regions: [] },
      basis: current.basis,
      disclosure: "CURRENT",
      context: {
        kind: "REMINDER",
        caseId: current.context.caseId,
        reminderId,
        revision: 9,
        tasks: ["REMINDER:REMINDER_UPDATE"],
        actionSeed: "Current action",
        dueDateSeed: "2026-11-05",
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
    succeeded({
      ...caseValue(),
      disclosure: "ERASED",
      screen: { regions: [] },
      context: {
        ...caseValue().context,
        revision: 101,
        workRevision: 0,
        tasks: [],
        editableInputs: [],
        seeds: { registration: null, decision: null, work: null, paymentCurrency: null },
      },
    }),
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
  expect(result.value.current.disclosure).toBe("ERASED");
  expect(get).not.toHaveBeenCalled();
});
