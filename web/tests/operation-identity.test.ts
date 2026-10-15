import { expect, it } from "vitest";
import { localNotice } from "../src/api/notices";
import { createDraft } from "../src/domain/metadata";
import { operationReducer, initialOperation } from "../src/domain/operationReducer";
import { groupedCorrectionValues, review, digest } from "./service-foundation.fixtures";
import {
  begin,
  initial,
  firstId,
  secondId,
  refused,
  reviewed,
  expectGuardedTransitionsInert,
} from "./operation-identity.fixtures";
it("unexposed edits retain identity but changes after a refusal get a fresh identity", () => {
  expect(
    operationReducer(initial(), {
      type: "EDIT",
      field: "claimantName",
      value: "B",
      nextOperationId: secondId,
    }).operationId,
  ).toBe(firstId);
  const rejected = refused(begin(initial(), 1), 1);
  const next = operationReducer(rejected, {
    type: "EDIT",
    field: "claimantName",
    value: "B",
    nextOperationId: secondId,
  });
  expect(next.operationId).toBe(secondId);
  expect(next.exposedRequest).toBeNull();
  expect(
    operationReducer(rejected, {
      type: "EDIT_REFERENCE",
      value: "changed",
      nextOperationId: secondId,
    }).operationId,
  ).toBe(secondId);
});
it("unknown delivery preserves exact request bytes and refuses edits while permitting retry", () => {
  const submitting = operationReducer(reviewed(), { type: "SUBMITTING", requestId: 2 });
  const unknown = operationReducer(submitting, {
    type: "OUTCOME_UNKNOWN",
    requestId: 2,
    message: localNotice("outcomeUnknown"),
  });
  expect(unknown.requestBytes).toBe(submitting.requestBytes);
  expect(unknown.exposedRequest).toBe(submitting.exposedRequest);
  expect(
    operationReducer(unknown, {
      type: "EDIT_REFERENCE",
      value: "changed",
      nextOperationId: secondId,
    }),
  ).toBe(unknown);
  const retry = operationReducer(unknown, { type: "SUBMITTING", requestId: 3 });
  expect(retry.delivery).toBe("SUBMITTING");
  expect(retry.requestBytes).toBe(unknown.requestBytes);
});
it("late responses and illegal transitions cannot replace the current operation", () => {
  const working = begin(initial(), 2);
  expect(
    operationReducer(working, { type: "REVIEWED", requestId: 1, requestSha256: digest, review }),
  ).toBe(working);
  expect(operationReducer(initial(), { type: "SUBMITTING", requestId: 1 })).toEqual(initial());
  const foreign = createDraft(secondId, "different", 0, "OPEN", {});
  const fresh = initial();
  expect(operationReducer(fresh, { type: "PREVIEWING", requestId: 3, draft: foreign })).toBe(fresh);
  expectGuardedTransitionsInert(fresh);
});
it("freezes all correction groups before exposure and forks after changed corrections", () => {
  const working = begin(
    initialOperation(firstId, "CORRECT_CASE", groupedCorrectionValues, "CASE-SYNTHETIC"),
    1,
  );
  const rejected = refused(working, 1);
  const next = operationReducer(rejected, {
    type: "EDIT_CORRECTION",
    group: "registration",
    field: "claimantName",
    value: "B",
    nextOperationId: secondId,
  });
  expect(next.operationId).toBe(secondId);
  expect(working.exposedRequest?.act).toMatchObject({
    groups: { registration: { mode: "REPLACE", values: { claimantName: "Synthetic A" } } },
  });
});
