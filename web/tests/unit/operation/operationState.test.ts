import { expect, it } from "vitest";
import {
  operationReducer,
  operationView,
  type OperationAction,
  type OperationState,
} from "../../../src/operation/operationState";
import {
  initial,
  begin,
  refused,
  reviewed,
  firstId,
  secondId,
  effect,
} from "../../support/operation-identity.fixtures";
import { wireReply } from "../../support/contract-corpus.fixtures";

const submitted = (state = reviewed(), id = "execute") =>
  operationReducer(operationReducer(state, { type: "EXECUTE", identity: effect(id) }), {
    type: "HANDOFF",
    identity: effect(id),
  });

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
  const submitting = submitted();
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

const unknown = () =>
  operationReducer(submitted(), {
    type: "REFUSE",
    identity: effect("execute"),
    notice: { kind: "local", reason: "outcomeUnknown" },
    target: null,
    uncertain: true,
    disclosure: false,
  });
it("fences stale execute acceptance after epoch or target change while retaining uncertain identity", () => {
  const submitting = submitted();
  const result = wireReply("operation.execute");
  if (result.outcome.kind !== "ACCEPTED") {
    throw new Error("Acceptance example missing");
  }
  for (const context of [
    { credential: "new-epoch", target: submitting.target },
    { credential: "token", target: "CASE:OTHER" },
  ]) {
    const invalidated = operationReducer(submitting, { type: "CONTEXT", epoch: 0, ...context });
    expect(invalidated.operation.kind).toBe("UNRESOLVED");
    const late = operationReducer(invalidated, {
      type: "RECORD",
      receipt: result.outcome.data,
      identity: effect("execute"),
    });
    expect(late).toBe(invalidated);
    expect(operationView(late).prepared).toBe(operationView(submitting).prepared);
    expect(operationView(late).receipt).toBeNull();
  }
});
it("disclosure invalidation removes a submitting review and its late completion cannot restore it", () => {
  const pending = submitted();
  expect(operationView(pending).review).not.toBeNull();
  const invalid = operationReducer(pending, { type: "INVALIDATE", refused: true });
  expect(operationView(invalid).review).toBeNull();
  expect(operationView(invalid).priorUncertain).toBe(true);
});
const controlEvents = (state: OperationState): OperationAction[] => [
  { type: "EDIT", path: "caseReference", value: "OTHER", id: "replacement" },
  { type: "RETURN" },
  { type: "CONFIRM", value: true },
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
  { type: "CONTEXT", epoch: 0, credential: state.context.credential, target: state.context.target },
];
it("200 deterministic event sequences preserve prior uncertainty and cannot schedule cleanup", () => {
  for (let seed = 1; seed <= 200; seed += 1) {
    let state = unknown();
    const { prepared } = operationView(state);
    const { effects } = state;
    let cursor = seed;
    for (let step = 0; step < 20; step += 1) {
      cursor = (Math.imul(cursor, 1664525) + 1013904223) >>> 0;
      const events = controlEvents(state);
      state = operationReducer(state, events[cursor % events.length]!);
      expect(operationView(state).priorUncertain).toBe(true);
      expect(operationView(state).prepared).toBe(prepared);
      expect(state.effects.every((value) => effects.includes(value))).toBe(true);
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
