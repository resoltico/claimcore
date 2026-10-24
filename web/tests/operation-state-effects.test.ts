import { expect, it } from "vitest";
import type { OperationAction, OperationState } from "../src/interaction/operationState";
import { operationReducer, operationView } from "../src/interaction/operationState";
import { initial, reviewed, effect } from "./operation-identity.fixtures";
import { wireReply } from "./contract-corpus.fixtures";
const unknown = () =>
  operationReducer(operationReducer(reviewed(), { type: "EXECUTE", identity: effect("execute") }), {
    type: "REFUSE",
    identity: effect("execute"),
    notice: { kind: "local", reason: "outcomeUnknown" },
    target: null,
    uncertain: true,
    disclosure: false,
  });
it("fences stale execute acceptance after epoch or target change while retaining uncertain identity", () => {
  const submitting = operationReducer(reviewed(), { type: "EXECUTE", identity: effect("execute") });
  const result = wireReply("operation.execute");
  if (result.outcome.tag !== "ACCEPTED") {
    throw new Error("Acceptance example missing");
  }
  for (const context of [
    { token: "new-epoch", target: submitting.target },
    { token: "token", target: "CASE:OTHER" },
  ]) {
    const invalidated = operationReducer(submitting, { type: "CONTEXT", ...context });
    expect(invalidated.operation.tag).toBe("UNCERTAIN");
    const late = operationReducer(invalidated, {
      type: "RECORD",
      receipt: result.outcome.data,
      identity: effect("execute"),
      recoveryId: null,
    });
    expect(late).toBe(invalidated);
    expect(operationView(late).prepared).toBe(operationView(submitting).prepared);
    expect(operationView(late).receipt).toBeNull();
  }
});
it("disclosure invalidation removes a submitting review and its late completion cannot restore it", () => {
  const pending = operationReducer(reviewed(), { type: "EXECUTE", identity: effect("execute") });
  expect(operationView(pending).review).not.toBeNull();
  const invalid = operationReducer(pending, { type: "INVALIDATE", refused: true });
  expect(operationView(invalid).review).toBeNull();
  expect(operationView(invalid).priorUncertain).toBe(true);
});
const controlEvents = (state: OperationState): OperationAction[] => [
  { type: "EDIT", path: "caseReference", value: "OTHER", id: "replacement" },
  { type: "RETURN" },
  { type: "CONFIRM", value: true },
  { type: "ACKNOWLEDGE", identity: effect("ack") },
  {
    type: "REFUSE",
    notice: { kind: "local", reason: "incomplete" },
    target: null,
    uncertain: false,
    disclosure: false,
  },
  { type: "INVALIDATE", refused: true },
  { type: "RESTORE" },
  { type: "RESET", state: initial(), abandon: false },
  { type: "CONTEXT", token: state.context.token, target: state.context.target },
];
it("200 deterministic event sequences preserve prior uncertainty and cannot schedule cleanup", () => {
  for (let seed = 1; seed <= 200; seed += 1) {
    let state = unknown();
    const { prepared } = operationView(state);
    let cursor = seed;
    for (let step = 0; step < 20; step += 1) {
      cursor = (Math.imul(cursor, 1664525) + 1013904223) >>> 0;
      const events = controlEvents(state);
      state = operationReducer(state, events[cursor % events.length]!);
      expect(operationView(state).priorUncertain).toBe(true);
      expect(operationView(state).prepared).toBe(prepared);
      expect(state.effects.some((value) => value.kind === "FORGET")).toBe(false);
    }
  }
});
it("duplicate execute events emit one ledger effect and mismatched effect IDs cannot complete it", () => {
  const first = operationReducer(reviewed(), { type: "EXECUTE", identity: effect("first") });
  const duplicate = operationReducer(first, { type: "EXECUTE", identity: effect("second") });
  expect(duplicate).toBe(first);
  expect(first.effects.filter((value) => value.kind === "EXECUTE")).toHaveLength(1);
  expect(
    operationReducer(first, {
      type: "REFUSE",
      identity: effect("second"),
      notice: { kind: "local", reason: "incomplete" },
      target: null,
      uncertain: false,
      disclosure: false,
    }),
  ).toBe(first);
});
