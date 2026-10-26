import { expect, it } from "vitest";
import { operationReducer, operationView } from "../src/operation/operationState";
import {
  initial,
  begin,
  refused,
  reviewed,
  firstId,
  secondId,
  effect,
} from "./operation-identity.fixtures";
it("unexposed edits keep identity and changed exposed input receives fresh identity", () => {
  expect(
    operationReducer(initial(), { type: "EDIT", path: "act.reason", value: "B", id: secondId })
      .values["operationId"],
  ).toBe(firstId);
  const exposed = refused();
  const changed = operationReducer(exposed, {
    type: "EDIT",
    path: "act.reason",
    value: "B",
    id: secondId,
  });
  expect(changed.values["operationId"]).toBe(secondId);
  expect(operationView(changed).prepared).toBeNull();
  expect(
    operationReducer(exposed, {
      type: "EDIT",
      path: "caseReference",
      value: "CASE-SERIALIZATION",
      id: secondId,
    }),
  ).toBe(exposed);
});
it("uncertainty survives later definite failures and blocks edits while allowing exact retry", () => {
  const submitting = operationReducer(reviewed(), { type: "EXECUTE", identity: effect("execute") });
  const unknown = operationReducer(submitting, {
    type: "REFUSE",
    notice: { kind: "local", reason: "outcomeUnknown" },
    target: null,
    uncertain: true,
    disclosure: false,
  });
  const rejected = refused(unknown);
  expect(operationView(rejected).priorUncertain).toBe(true);
  expect(operationView(rejected).prepared).toBe(operationView(submitting).prepared);
  expect(
    operationReducer(rejected, {
      type: "EDIT",
      path: "caseReference",
      value: "OTHER",
      id: secondId,
    }),
  ).toBe(rejected);
  expect(
    operationView(operationReducer(rejected, { type: "EXECUTE", identity: effect("execute") }))
      .phase,
  ).toBe("UNRESOLVED");
});
it("requires an exact review and consent and blocks edits while an attempt is pending", () => {
  expect(operationReducer(initial(), { type: "EXECUTE", identity: effect("execute") })).toEqual(
    initial(),
  );
  expect(operationView(operationReducer(begin(), { type: "CONFIRM", value: true })).confirmed).toBe(
    false,
  );
  const original = reviewed();
  const pending = operationReducer(original, { type: "EXECUTE", identity: effect("execute") });
  expect(operationView(pending).prepared).toBe(operationView(original).prepared);
  expect(
    operationReducer(pending, {
      type: "EDIT",
      path: "caseReference",
      value: "OTHER",
      id: secondId,
    }),
  ).toBe(pending);
});
