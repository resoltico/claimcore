import { expect } from "vitest";
import { localNotice } from "../src/api/notices";
import { createDraft } from "../src/domain/metadata";
import {
  initialOperation,
  operationReducer,
  type OperationState,
} from "../src/domain/operationReducer";
import { groupedCorrectionValues, preparation, review } from "./v3-foundation.fixtures";

export const firstId = "00000000-0000-4000-8000-000000000001";
export const secondId = "00000000-0000-4000-8000-000000000002";
export const initial = () =>
  initialOperation(firstId, "OPEN", { claimantName: "Synthetic A" }, "CASE-SYNTHETIC");
export const begin = (state: OperationState, requestId: number) =>
  operationReducer(state, {
    type: "PREPARING",
    requestId,
    draft:
      state.exposedRequest ??
      createDraft(state.operationId, state.caseReference, "0", state.command, state.values),
  });
export const refused = (state: OperationState, requestId: number) =>
  operationReducer(state, {
    type: "DEFINITELY_REJECTED",
    requestId,
    message: localNotice("unreachable"),
    field: null,
  });
export const reviewed = () =>
  operationReducer(begin(initial(), 1), {
    type: "PREPARED",
    requestId: 1,
    preparation,
    review,
  });

const correctionActions = () => ({
  edit: (group: "registration" | "decision", value: string) => ({
    type: "EDIT_CORRECTION" as const,
    group,
    field: "claimantName",
    value,
    nextOperationId: secondId,
  }),
  mode: (value: "KEEP" | "REPLACE") => ({
    type: "SET_CORRECTION_MODE" as const,
    group: "registration" as const,
    mode: value,
    nextOperationId: secondId,
  }),
});

const expectCorrectionGuards = (flat: OperationState) => {
  const grouped = initialOperation(
    firstId,
    "CORRECT_CASE",
    groupedCorrectionValues,
    "CASE-SYNTHETIC",
  );
  const working = begin(grouped, 1);
  const { edit, mode } = correctionActions();
  expect(operationReducer(flat, edit("registration", "Synthetic B"))).toBe(flat);
  expect(operationReducer(flat, mode("KEEP"))).toBe(flat);
  expect(operationReducer(working, edit("registration", "Synthetic B"))).toBe(working);
  expect(operationReducer(working, mode("KEEP"))).toBe(working);
  expect(operationReducer(grouped, edit("registration", "Synthetic A"))).toBe(grouped);
  expect(operationReducer(grouped, edit("decision", "Synthetic B"))).toBe(grouped);
  expect(operationReducer(grouped, mode("REPLACE"))).toBe(grouped);
  expect(operationReducer(grouped, edit("registration", "Synthetic B")).operationId).toBe(firstId);
  expect(operationReducer(grouped, mode("KEEP")).operationId).toBe(firstId);
};

/** Transitions that must change nothing, and the identity an unexposed edit keeps. */
export const expectGuardedTransitionsInert = (fresh: OperationState) => {
  expect(operationReducer(fresh, { type: "KEEP_FOR_RECOVERY" })).toBe(fresh);
  expect(
    operationReducer(fresh, {
      type: "EDIT_REFERENCE",
      value: fresh.caseReference,
      nextOperationId: secondId,
    }),
  ).toBe(fresh);
  const rejectedWithField = operationReducer(begin(fresh, 1), {
    type: "DEFINITELY_REJECTED",
    requestId: 1,
    message: localNotice("unreachable"),
    field: "claimantName",
  });
  expect(rejectedWithField.fieldError?.name).toBe("claimantName");
  expect(refused(begin(fresh, 1), 1).fieldError).toBeNull();
  expectCorrectionGuards(fresh);
};
