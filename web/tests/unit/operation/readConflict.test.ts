import { operationAffordance } from "../../support/affordances.fixtures";
import { testWorkspaceIdentity } from "../../support/presentation-test-support";
import { expect, it } from "vitest";
import { readConflict } from "../../../src/operation/readConflict";
import { mockEndpoint } from "../../support/endpoint-mocks";
import { current } from "../../support/operation-form.fixtures";
import { prepareRequest } from "../../../src/protocol/preparedRequest";
import type { OperationSubmission } from "@contracts/client";
import type {
  ConflictSource,
  LocalSource,
  OperationEffect,
} from "../../../src/operation/operationTypes";
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
  subject: { ...current.subject, revision: 100, workRevision: 7 },
  history: "NONE" as const,
  actions: current.actions.filter((action) =>
    new Set<string>(["work.update"] as const).has(action.task),
  ),
});
const scoped = (source: LocalSource): source is ConflictSource =>
  source.prepared.input.binding.kind !== "PEOPLE";
const effectFor = (
  input: OperationSubmission,
): Extract<OperationEffect, { kind: "READ_CONFLICT" }> => {
  const source = { prepared: prepareRequest("operation.execute", input), submittedRows: {} };
  if (!scoped(source)) {
    throw new Error("Expected a case-bound operation.");
  }
  const identity = {
    owner: {
      serviceOrigin: location.origin,
      installationId: testWorkspaceIdentity.installationId,
      actorId: testWorkspaceIdentity.actorId,
      sessionEpoch: 0,
    },
    id: "read-conflict",
    contextGeneration: 0,
    credential: "csrf",
    target: "bound-case",
  };
  return {
    kind: "READ_CONFLICT",
    identity,
    conflictId: identity.id,
    source,
    target: { kind: "ID", caseId: current.subject.caseId },
    minimumRevision: 5,
  };
};
const root = {
  operationId: "10000000-0000-4000-8000-000000000001",
  target: { kind: "REFERENCE" as const, caseReference: "CC-55KW-SSJ4-8HMS-G1BH-73" },
};
it("uses the current work revision separately from the case fact revision", async () => {
  mockEndpoint("case.get").mockResolvedValue(succeeded(caseValue()));
  const reminder = mockEndpoint("reminder.get");
  const reads = new Map<string, AbortController>();
  const result = await readConflict(
    effectFor({
      ...root,
      task: "work.update",
      binding: { kind: "WORK", target: root.target, expectedRevision: 1 },
      inputs: { "field:handlingSummary": "Retained summary" },
    }),
    reads,
  );
  expect(result.type).toBe("CONFLICT_OBSERVED");
  if (result.type !== "CONFLICT_OBSERVED") {
    throw new Error("Expected refreshed work.");
  }
  expect(result.value.revision).toBe(7);
  expect(result.value.current.subject.revision).toBe(100);
  expect(reminder).not.toHaveBeenCalled();
  expect(reads.size).toBe(0);
});
it("reads the selected reminder revision rather than either case or work revision", async () => {
  const parent = mockEndpoint("case.get").mockResolvedValue(succeeded(caseValue()));
  const reminderId = "30000000-0000-4000-8000-000000000003";
  const get = mockEndpoint("reminder.get").mockResolvedValue(
    succeeded({
      screen: { regions: [] },
      basis: current.basis,
      disclosure: "CURRENT",
      subject: { kind: "LIST" },
      actions: [
        operationAffordance(
          "reminder.update",
          {
            kind: "REMINDER",
            target: { kind: "ID", caseId: current.subject.caseId },
            reminderId,
            expectedRevision: 9,
          },
          {},
          undefined,
          {},
        ),
      ],
      links: [],
      history: null,
      nextCursor: null,
    }),
  );
  const result = await readConflict(
    effectFor({
      ...root,
      task: "reminder.update",
      binding: {
        kind: "REMINDER",
        target: root.target,
        reminderId,
        expectedRevision: 1,
      },
      inputs: { "input:reminderAction": "Retained action", "input:reminderDueDate": "2026-11-06" },
    }),
    new Map(),
  );
  expect(result.type).toBe("CONFLICT_OBSERVED");
  if (result.type !== "CONFLICT_OBSERVED") {
    throw new Error("Expected refreshed reminder.");
  }
  expect(result.value.revision).toBe(9);
  expect(parent.mock.calls[0]?.[1]).toEqual({
    target: { kind: "ID", caseId: current.subject.caseId },
  });
  expect(get.mock.calls[0]?.[1]).toEqual({
    target: { kind: "ID", caseId: current.subject.caseId },
    reminderId,
  });
});
it("retains an erased current-case status without inventing editable facts or a reminder revision", async () => {
  mockEndpoint("case.get").mockResolvedValue(
    succeeded({
      ...caseValue(),
      disclosure: "ERASED",
      screen: { regions: [] },
      subject: { ...caseValue().subject, revision: 101, workRevision: 0 },
      actions: caseValue().actions.filter((action) => new Set<string>([]).has(action.task)),
    }),
  );
  const get = mockEndpoint("reminder.get");
  const result = await readConflict(
    effectFor({
      ...root,
      task: "reminder.cancel",
      binding: {
        kind: "REMINDER",
        target: root.target,
        reminderId: "30000000-0000-4000-8000-000000000003",
        expectedRevision: 1,
      },
      inputs: {},
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

it("rejects a different returned case without reading the reminder", async () => {
  mockEndpoint("case.get").mockResolvedValue(
    succeeded({
      ...caseValue(),
      subject: { ...caseValue().subject, caseId: "90000000-0000-4000-8000-000000000004" },
    }),
  );
  const reminder = mockEndpoint("reminder.get");
  const result = await readConflict(
    effectFor({
      ...root,
      task: "reminder.cancel",
      binding: {
        kind: "REMINDER",
        target: root.target,
        reminderId: "30000000-0000-4000-8000-000000000003",
        expectedRevision: 1,
      },
      inputs: {},
    }),
    new Map(),
  );
  expect(result.type).toBe("CONFLICT_READ_FAILED");
  expect(reminder).not.toHaveBeenCalled();
});
