import { expect, it } from "vitest";
import { operationReducer, operationView } from "../../../src/operation/operationState";
import { latestOperationValues } from "../../../src/operation/operationConflict";
import { operationEntry } from "../../../src/protocol/operationCatalog";
import { prepareRequest } from "../../../src/protocol/preparedRequest";
import {
  begin,
  effect,
  reviewed,
  firstId,
  secondId,
} from "../../support/operation-identity.fixtures";
import { current } from "../../support/operation-form.fixtures";
import type { Diagnostic_REVISION_CONFLICT } from "@contracts/client";
const diagnostic: Diagnostic_REVISION_CONFLICT = {
  code: "REVISION_CONFLICT",
  action: "REVIEW_LATEST",
  actualRevision: 2,
  changes: { kind: "WITHHELD" },
};
const conflicted = () =>
  operationReducer(begin(), {
    type: "REFUSE",
    identity: effect(),
    notice: { kind: "diagnostic", diagnostic },
    target: null,
    uncertain: false,
    disclosure: false,
  });
it("changes only the declared identity/assumption while preserving captured authoring format", () => {
  const amount = { kind: "LOCALIZED_AMOUNT" as const, text: "1.234,56", locale: "de-DE" as const };
  const values = {
    operationId: firstId,
    expectedRevision: "1",
    "act.values.claimed.amount": amount,
    "target.caseReference": "CASE-SERIALIZATION",
  };
  const result = latestOperationValues(
    operationEntry({ subject: "CASE", kind: "AMEND_REGISTRATION" }),
    values,
    8,
    secondId,
  );
  expect(result["operationId"]).toBe(secondId);
  expect(result["expectedRevision"]).toBe("8");
  expect(result["act.values.claimed.amount"]).toBe(amount);
  expect(result["target.caseReference"]).toBe("CASE-SERIALIZATION");
});
it("does not rebase an uncertain request when its exact retry returns a revision conflict", () => {
  let state = operationReducer(reviewed(), { type: "EXECUTE", identity: effect("send") });
  state = operationReducer(state, { type: "HANDOFF", identity: effect("send") });
  state = operationReducer(state, {
    type: "REFUSE",
    identity: effect("send"),
    notice: { kind: "local", reason: "outcomeUnknown" },
    target: null,
    uncertain: true,
    disclosure: false,
  });
  const { bytes } = operationView(state).prepared!;
  state = operationReducer(state, { type: "EXECUTE", identity: effect("retry") });
  state = operationReducer(state, {
    type: "REFUSE",
    identity: effect("retry"),
    notice: { kind: "diagnostic", diagnostic },
    target: null,
    uncertain: false,
    disclosure: false,
  });
  expect(state.conflict).toBeNull();
  expect(operationView(state).phase).toBe("UNRESOLVED");
  expect(operationView(state).prepared!.bytes).toBe(bytes);
  expect(state.effects.some((entry) => entry.kind === "READ_CONFLICT")).toBe(false);
});
it("fences refreshed conflict observations by correlation identity and session", () => {
  const state = conflicted();
  expect(state.conflict).not.toBeNull();
  const snapshot = state.conflict!;
  const value = { current: { ...current, revision: 3 }, revision: 3 };
  expect(
    operationReducer(state, {
      type: "CONFLICT_OBSERVED",
      identity: effect(),
      conflictId: "different-conflict",
      value,
    }),
  ).toBe(state);
  const replacement = operationReducer(state, {
    type: "CONTEXT",
    epoch: 1,
    credential: "different-session",
    target: state.target,
  });
  expect(replacement.conflict).toBeNull();
  expect(
    operationReducer(replacement, {
      type: "CONFLICT_OBSERVED",
      identity: effect(),
      conflictId: snapshot.identity.id,
      value,
    }),
  ).toBe(replacement);
});
it("refuses implicit preview and re-preparation for a different case target", () => {
  const state = conflicted();
  const snapshot = state.conflict!;
  const observed = operationReducer(state, {
    type: "CONFLICT_OBSERVED",
    identity: effect(),
    conflictId: snapshot.identity.id,
    value: { current: { ...current, revision: 3 }, revision: 3 },
  });
  const prepared = prepareRequest("operation.execute", {
    kind: "CASE",
    operationId: secondId,
    target: { kind: "REFERENCE", caseReference: "OTHER-CASE" },
    expectedRevision: 3,
    act: { kind: "CLOSE" },
  });
  expect(
    operationReducer(observed, {
      type: "REVIEW_LATEST",
      identity: effect("new-review"),
      conflictId: snapshot.identity.id,
      generation: observed.generation,
      values: observed.values,
      prepared,
    }),
  ).toBe(observed);
  expect(
    operationReducer(observed, {
      type: "PREVIEW",
      identity: effect("implicit"),
      generation: observed.generation,
      prepared,
    }),
  ).toBe(observed);
});
