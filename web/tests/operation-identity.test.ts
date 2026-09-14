import { expect, it } from "vitest";
import { createDraft } from "../src/domain/metadata";
import {
  initialOperation,
  operationReducer,
  type OperationState,
} from "../src/domain/operationReducer";
import { caseFields, preparation, review } from "./v2-foundation.fixtures";

const firstId = "00000000-0000-4000-8000-000000000001";
const secondId = "00000000-0000-4000-8000-000000000002";
const initial = () =>
  initialOperation(firstId, "OPEN", { claimantName: "Synthetic A" }, "CASE-SYNTHETIC");
const begin = (state: OperationState, requestId: number) =>
  operationReducer(state, {
    type: "PREPARING",
    requestId,
    draft:
      state.exposedRequest ??
      createDraft(state.operationId, state.caseReference, "0", state.command, state.values),
  });
const refused = (state: OperationState, requestId: number) =>
  operationReducer(state, {
    type: "DEFINITELY_REJECTED",
    requestId,
    message: "Synthetic refusal",
    field: null,
  });
const reviewed = () =>
  operationReducer(begin(initial(), 1), {
    type: "PREPARED",
    requestId: 1,
    preparation,
    review,
  });

it("forks a new ID when an exposed request is edited after definite submit refusal", () => {
  const submitting = operationReducer(reviewed(), { type: "SUBMITTING", requestId: 2 });
  const rejected = refused(submitting, 2);
  const edited = operationReducer(rejected, {
    type: "EDIT",
    field: "claimantName",
    value: "Synthetic B",
    nextOperationId: secondId,
  });
  expect(edited).toMatchObject({
    delivery: "EDITING",
    operationId: secondId,
    values: { claimantName: "Synthetic B" },
    exposedRequest: null,
  });
  expect(rejected.exposedRequest?.operationId).toBe(firstId);
});

it("forks on a changed reference or command, but not on an unchanged field", () => {
  const rejected = refused(begin(initial(), 1), 1);
  expect(
    operationReducer(rejected, {
      type: "EDIT",
      field: "claimantName",
      value: "Synthetic A",
      nextOperationId: secondId,
    }),
  ).toBe(rejected);
  const reference = operationReducer(rejected, {
    type: "EDIT_REFERENCE",
    value: "CASE-OTHER",
    nextOperationId: secondId,
  });
  expect(reference.operationId).toBe(secondId);
  expect(reference.caseReference).toBe("CASE-OTHER");
  const command = operationReducer(rejected, {
    type: "CHANGE_COMMAND",
    command: "DECIDE",
    values: { payableAmount: "1.00" },
    nextOperationId: secondId,
  });
  expect(command.operationId).toBe(secondId);
  expect(command.command).toBe("DECIDE");
});

it("retains the frozen exact request on unchanged retry after unknown preparation", () => {
  const first = begin(initial(), 1);
  const unknown = operationReducer(first, {
    type: "PREPARATION_UNKNOWN",
    requestId: 1,
    message: "Synthetic delivery loss",
  });
  const retry = begin(unknown, 2);
  expect(retry.delivery).toBe("PREPARING");
  expect(retry.exposedRequest).toBe(first.exposedRequest);
  expect(retry.operationId).toBe(firstId);
  if (retry.exposedRequest?.command.kind !== "OPEN") throw new Error("Expected an OPEN request.");
  expect(retry.exposedRequest.command.values.claimantName).toBe("Synthetic A");
  expect(
    operationReducer(unknown, {
      type: "EDIT_REFERENCE",
      value: "CASE-OTHER",
      nextOperationId: secondId,
    }),
  ).toBe(unknown);
});

it("retries an unchanged definite refusal with its original exact identity", () => {
  const first = begin(initial(), 1);
  const rejected = refused(first, 1);
  const retry = begin(rejected, 2);
  expect(retry.operationId).toBe(firstId);
  expect(retry.exposedRequest).toBe(first.exposedRequest);
  expect(retry.pending).toEqual({ kind: "PREPARE", requestId: 2 });
});

it("ignores late prepare and submit responses from inactive attempts", () => {
  const unknown = operationReducer(begin(initial(), 1), {
    type: "PREPARATION_UNKNOWN",
    requestId: 1,
    message: "Synthetic delivery loss",
  });
  const retry = begin(unknown, 2);
  expect(operationReducer(retry, { type: "PREPARED", requestId: 1, preparation, review })).toBe(
    retry,
  );
  const reviewing = operationReducer(retry, {
    type: "PREPARED",
    requestId: 2,
    preparation,
    review,
  });
  const submitting = operationReducer(reviewing, { type: "SUBMITTING", requestId: 3 });
  const rejected = refused(submitting, 3);
  expect(
    operationReducer(rejected, {
      type: "ACCEPTED",
      requestId: 3,
      receipt: {
        operationId: firstId,
        snapshot: { fields: caseFields, revision: "1" },
        recordedAt: "2026-09-09T00:00:00.0000000+00:00",
        recordedBy: "synthetic",
        replayed: false,
        command: "OPEN",
      },
    }),
  ).toBe(rejected);
});

it("retains an exact id until a retained preparation is edited, then starts a new id", () => {
  const unexposed = operationReducer(initial(), {
    type: "EDIT_REFERENCE",
    value: "CASE-OTHER",
    nextOperationId: secondId,
  });
  expect(unexposed.operationId).toBe(firstId);
  const reviewing = operationReducer(begin(unexposed, 1), {
    type: "PREPARED",
    requestId: 1,
    preparation,
    review,
  });
  const retained = operationReducer(reviewing, { type: "KEEP_FOR_RECOVERY" });
  const edited = operationReducer(retained, {
    type: "EDIT_REFERENCE",
    value: "CASE-THIRD",
    nextOperationId: secondId,
  });
  expect(edited).toMatchObject({
    operationId: secondId,
    delivery: "EDITING",
    exposedRequest: null,
  });
});

it("models definite rejection and unknown outcomes without allowing a dispatched mutation to regress", () => {
  const unknown = operationReducer(begin(initial(), 1), {
    type: "PREPARATION_UNKNOWN",
    requestId: 1,
    message: "Synthetic delivery loss",
  });
  expect(
    operationReducer(unknown, {
      type: "EDIT",
      field: "claimantName",
      value: "Synthetic B",
      nextOperationId: secondId,
    }),
  ).toBe(unknown);
  const changed = operationReducer(refused(begin(initial(), 2), 2), {
    type: "CHANGE_COMMAND",
    command: "DECIDE",
    values: { payableAmount: "1.00" },
    nextOperationId: secondId,
  });
  expect(changed).toMatchObject({
    delivery: "EDITING",
    operationId: secondId,
    command: "DECIDE",
  });
  const reviewing = operationReducer(begin(changed, 3), {
    type: "PREPARED",
    requestId: 3,
    preparation,
    review,
  });
  const completed = operationReducer(
    operationReducer(reviewing, { type: "SUBMITTING", requestId: 4 }),
    { type: "OUTCOME_UNKNOWN", requestId: 4, message: "Synthetic uncertainty" },
  );
  expect(completed.delivery).toBe("OUTCOME_UNKNOWN");
  expect(operationReducer(completed, { type: "RESET_MESSAGE" }).message).toBeNull();
});

it("keeps invalid reducer transitions inert and records a completed receipt", () => {
  const fresh = initial();
  expect(operationReducer(fresh, { type: "SUBMITTING", requestId: 1 })).toBe(fresh);
  expect(operationReducer(fresh, { type: "PREPARED", requestId: 1, preparation, review })).toBe(
    fresh,
  );
  const reviewing = reviewed();
  expect(begin(reviewing, 2)).toBe(reviewing);
  const accepted = operationReducer(
    operationReducer(reviewing, { type: "SUBMITTING", requestId: 2 }),
    {
      type: "ACCEPTED",
      requestId: 2,
      receipt: {
        operationId: firstId,
        snapshot: { fields: caseFields, revision: "1" },
        recordedAt: "2026-09-09T00:00:00.0000000+00:00",
        recordedBy: "synthetic",
        replayed: false,
        command: "OPEN",
      },
    },
  );
  expect(accepted.delivery).toBe("ACCEPTED");
});

it("freezes grouped correction identity while a later correction edit forks a new operation", () => {
  const correction = {
    registration: {
      mode: "REPLACE" as const,
      values: {
        incidentDate: "2026-09-01",
        incidentNotificationDate: "2026-09-02",
        incidentCountry: "Latvia",
        claimantName: "Synthetic A",
        insurerName: "Synthetic insurer",
        claimedAmount: "12.34",
        claimedCurrency: "EUR",
      },
    },
    decision: { mode: "KEEP" as const, values: {} },
    payment: { mode: "KEEP" as const, values: {} },
  };
  const first = begin(initialOperation(firstId, "CORRECT_CASE", correction, "CASE-SYNTHETIC"), 1);
  if (first.exposedRequest?.command.kind !== "CORRECT_CASE")
    throw new Error("Expected a grouped correction request.");
  const rejected = refused(first, 1);
  const edited = operationReducer(rejected, {
    type: "EDIT_CORRECTION",
    group: "registration",
    field: "claimantName",
    value: "Synthetic B",
    nextOperationId: secondId,
  });
  if (rejected.exposedRequest?.command.kind !== "CORRECT_CASE")
    throw new Error("Expected retained grouped correction identity.");
  expect(rejected.exposedRequest.command.groups.registration).toEqual({
    mode: "REPLACE",
    values: { ...correction.registration.values },
  });
  expect(edited.operationId).toBe(secondId);
  expect(edited.values).toMatchObject({
    registration: { mode: "REPLACE", values: { claimantName: "Synthetic B" } },
  });
});
