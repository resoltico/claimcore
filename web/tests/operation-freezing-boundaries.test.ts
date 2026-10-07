import { expect, it } from "vitest";
import { createDraft } from "../src/domain/metadata";
import { freezeRequest } from "../src/domain/operationRequest";
import { initialOperation, operationReducer } from "../src/domain/operationReducer";
import { firstId, secondId, refused, begin, reviewed } from "./operation-identity.fixtures";
import { groupedCorrectionValues } from "./v3-foundation.fixtures";

it("detaches replacement group values so later authoring cannot change a frozen request", () => {
  const authored = {
    ...groupedCorrectionValues,
    registration: {
      mode: "REPLACE" as const,
      values: { claimantName: "Before", claimedAmount: "1.0000" },
    },
  };
  const draft = createDraft(firstId, "CASE-1", "3", "CORRECT_CASE", authored);
  const frozen = freezeRequest(draft);
  if (
    draft.command.kind !== "CORRECT_CASE" ||
    draft.command.groups.registration.mode !== "REPLACE"
  ) {
    throw new Error("Missing authored correction fixture.");
  }
  Object.assign(draft.command.groups.registration.values, {
    claimantName: "After",
    claimedAmount: "2",
  });
  if (
    frozen.command.kind !== "CORRECT_CASE" ||
    frozen.command.groups.registration.mode !== "REPLACE"
  ) {
    throw new Error("Missing correction fixture.");
  }
  expect(frozen.command.groups.registration.values.claimantName).toBe("Before");
  expect(frozen.command.groups.registration.values.claimedAmount).toBe("1.0000");
});
it("forks exposed correction identities when their group modes change after definite refusal", () => {
  const initial = initialOperation(firstId, "CORRECT_CASE", groupedCorrectionValues, "CASE-1");
  const exposed = refused(begin(initial, 1), 1);
  const changed = operationReducer(exposed, {
    type: "SET_CORRECTION_MODE",
    group: "payment",
    mode: "CLEAR",
    nextOperationId: secondId,
  });
  expect(changed.operationId).toBe(secondId);
  expect(changed.exposedRequest).toBeNull();
});
it("refuses submission transitions when no exact reviewed request exists", () => {
  const editing = initialOperation(firstId, "CLOSE", {}, "CASE-1");
  expect(operationReducer(editing, { type: "SUBMITTING", requestId: 9 })).toBe(editing);
  const reviewing = { ...editing, delivery: "REVIEWING" as const, exposedRequest: null };
  expect(operationReducer(reviewing, { type: "SUBMITTING", requestId: 9 })).toBe(reviewing);
});
it("enters submitting with the exact reviewed request and makes authoring unavailable", () => {
  const reviewing = reviewed();
  const submitting = operationReducer(reviewing, { type: "SUBMITTING", requestId: 9 });
  expect(submitting).toEqual({
    ...reviewing,
    delivery: "SUBMITTING",
    message: null,
    fieldError: null,
    pending: { kind: "SUBMIT", requestId: 9 },
  });
  expect(submitting.exposedRequest).toBe(reviewing.exposedRequest);
  expect(
    operationReducer(submitting, {
      type: "EDIT_REFERENCE",
      value: "OTHER",
      nextOperationId: secondId,
    }),
  ).toBe(submitting);
});
