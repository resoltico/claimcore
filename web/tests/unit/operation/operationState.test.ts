import { prepareRequest } from "../../../src/protocol/preparedRequest";
import { caseFields } from "../../support/service-foundation.fixtures";
import { actorRefInput, workBasis } from "../../support/inputs";
import type { OperationPhase } from "../../../src/operation/operationTypes";
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
import { wireReply } from "../../support/reply.fixtures";

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
      path: "target.caseReference",
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
      path: "target.caseReference",
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
      path: "target.caseReference",
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
      receipt: { ...result.outcome.data, view: result.outcome.view },
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
  { type: "EDIT", path: "target.caseReference", value: "OTHER", id: "replacement" },
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

it("keeps refused disclosure masked after transient read and conflict failures until a positive read", () => {
  const identity = effect("reread");
  const state = { ...initial(), disclosureRefused: true, disclosureRead: identity };
  const failed = operationReducer(state, {
    type: "DISCLOSURE_READ_FAILED",
    identity,
    notice: { kind: "local", reason: "unreachable" },
    refused: false,
  });
  expect(failed.disclosureRefused).toBe(true);
  expect(failed.disclosureRevision).toBe(state.disclosureRevision + 1);
  expect(operationReducer(failed, { type: "INVALIDATE", refused: false }).disclosureRefused).toBe(
    true,
  );
  const restored = operationReducer(
    { ...failed, disclosureRead: identity },
    { type: "DISCLOSED", identity, value: initial().caseObservation! },
  );
  expect(restored.disclosureRefused).toBe(false);
  const conflict = operationReducer(begin(), {
    type: "REFUSE",
    identity: effect(),
    notice: {
      kind: "diagnostic",
      diagnostic: {
        code: "REVISION_CONFLICT",
        action: "REVIEW_LATEST",
        actualRevision: 2,
        changes: { kind: "WITHHELD" },
      },
    },
    uncertain: false,
    disclosure: false,
    target: null,
  });
  expect(conflict.conflict).not.toBeNull();
  const conflictFailed = operationReducer(
    { ...conflict, disclosureRefused: true, disclosureRead: identity },
    {
      type: "CONFLICT_READ_FAILED",
      identity,
      conflictId: conflict.conflict!.identity.id,
      notice: { kind: "local", reason: "unreachable" },
      refused: false,
    },
  );
  expect(conflictFailed.disclosureRefused).toBe(true);
  expect(conflictFailed.disclosureRevision).toBe(conflict.disclosureRevision + 1);
});

it("discarding authored intent cannot restore a refused disclosure without a positive observation", () => {
  const initialState = initial();
  const refusedState = { ...initialState, disclosureRefused: true };
  const resetState = operationReducer(refusedState, {
    type: "RESET",
    state: initial(),
    abandon: false,
  });
  expect(resetState.disclosureRefused).toBe(true);
  expect(resetState.disclosureRevision).toBe(refusedState.disclosureRevision + 1);
  expect(resetState.disclosureContext).toBe(refusedState.disclosureContext);
  const identity = effect("explicit-current-read");
  const restored = operationReducer(
    { ...resetState, disclosureRead: identity },
    { type: "DISCLOSED", identity, value: initialState.caseObservation! },
  );
  expect(restored.disclosureRefused).toBe(false);
});

const creationRefresh = (): OperationState => {
  const state = initial();
  const prior = {
    ...state.context,
    credential: "prior-token",
    epoch: 0,
    target: "CASE:verified-owner:NEW",
  };
  return {
    ...state,
    entry: { subject: "CASE", kind: "OPEN" },
    target: prior.target,
    context: { ...prior, credential: "current-token", epoch: 1, generation: 1 },
    disclosureContext: prior,
    disclosureRefused: true,
    caseObservation: null,
    dateEntries: {
      "act.values.incidentDate": { canonical: "", parts: [{ type: "day", value: 12 }] },
    },
  };
};
const creationIdentity = (state: OperationState) => ({
  id: "current-context-restore",
  contextGeneration: state.context.generation,
  credential: state.context.credential,
  target: state.context.target,
});
it("restores only owned creation entry after a fenced epoch refresh without restoring a review or case facts", () => {
  const state = creationRefresh();
  const restored = operationReducer(state, {
    type: "RESTORE_CREATION_DRAFT",
    editing: state.operation,
    disclosureRevision: state.disclosureRevision,
    identity: creationIdentity(state),
  });
  expect(restored.disclosureContext).toBe(state.context);
  expect(restored.disclosureRefused).toBe(false);
  expect(restored.values).toBe(state.values);
  expect(restored.dateEntries).toBe(state.dateEntries);
  expect(restored.caseObservation).toBeNull();
  expect(operationView(restored).review).toBeNull();
  expect(restored.effects).toEqual([]);
});
it.each([
  "same-epoch",
  "credential-only",
  "intervening-refusal",
  "different-target",
  "existing-case",
  "other-act",
  "stale-identity",
] as const)("does not authorize creation entry from %s", (kind) => {
  let state = creationRefresh();
  const observedRevision = state.disclosureRevision;
  if (kind === "same-epoch" || kind === "credential-only") {
    state = operationReducer(state, { type: "INVALIDATE", refused: true });
  }
  if (kind === "credential-only") {
    state = { ...state, context: { ...state.context, epoch: state.disclosureContext.epoch } };
  }
  const editing = state.operation;
  if (kind === "intervening-refusal") {
    state = operationReducer(state, { type: "INVALIDATE", refused: true });
  }
  if (kind === "same-epoch") {
    state = {
      ...state,
      context: {
        ...state.context,
        credential: state.disclosureContext.credential,
        epoch: state.disclosureContext.epoch,
      },
    };
  }
  if (kind === "different-target") {
    state = { ...state, context: { ...state.context, target: "CASE:different-owner:NEW" } };
  }
  if (kind === "existing-case") {
    state = { ...state, caseObservation: initial().caseObservation };
  }
  if (kind === "other-act") {
    state = { ...state, entry: { subject: "CASE", kind: "CLOSE" } };
  }
  const identity = creationIdentity(state);
  const restored = operationReducer(state, {
    type: "RESTORE_CREATION_DRAFT",
    editing,
    disclosureRevision: observedRevision,
    identity:
      kind === "stale-identity"
        ? { ...identity, contextGeneration: identity.contextGeneration - 1 }
        : identity,
  });
  expect(restored).toBe(state);
  expect(restored.disclosureRefused).toBe(true);
});
it("keeps date entry ownership through unrelated edits and clears only omitted or reset UI state", () => {
  const state = {
    ...initial(),
    dateEntries: {
      "act.values.incidentDate": { canonical: "", parts: [{ type: "day" as const, value: 12 }] },
      "act.values.incidentNotificationDate": {
        canonical: "",
        parts: [{ type: "month" as const, value: 10 }],
      },
    },
  };
  const edited = operationReducer(state, {
    type: "EDIT",
    path: "act.values.claimantName",
    value: "Authored name",
    id: secondId,
  });
  expect(edited.dateEntries).toBe(state.dateEntries);
  const omitted = operationReducer(edited, {
    type: "EDIT_FIELDS",
    values: {},
    omitted: ["act.values.incidentDate"],
    id: secondId,
  });
  expect(omitted.dateEntries).toEqual({
    "act.values.incidentNotificationDate": { canonical: "", parts: [{ type: "month", value: 10 }] },
  });
  expect(
    operationReducer(omitted, { type: "RESET", state: initial(), abandon: false }).dateEntries,
  ).toEqual({});
  expect(operationReducer(state, { type: "CLEAR_DATE_ENTRIES" }).dateEntries).toEqual({});
});
it("a partial date replaces a stale canonical value without adding network effects and is fenced after disclosure loss", () => {
  const path = "act.values.incidentDate";
  const state = { ...initial(), values: { ...initial().values, [path]: "2024-02-29" } };
  const action: OperationAction = {
    type: "DATE_ENTRY",
    path,
    canonical: "2024-02-29",
    parts: [
      { type: "month", value: 2 },
      { type: "day", value: 29 },
    ],
    id: secondId,
  };
  const partial = operationReducer(state, action);
  expect(partial.values[path]).toBe("");
  expect(partial.dateEntries[path]).toEqual({
    canonical: "",
    parts: [
      { type: "month", value: 2 },
      { type: "day", value: 29 },
    ],
  });
  expect(partial.effects).toBe(state.effects);
  expect(operationReducer({ ...state, disclosureRefused: true }, action).dateEntries).toEqual({});
  expect(operationReducer(state, { ...action, canonical: "stale-value" })).toBe(state);
});

it.each(["PREVIEWING", "REVIEWING", "PREPARING", "SUBMITTING", "UNRESOLVED", "ACCEPTED"] as const)(
  "does not restore editable creation entry while %s",
  (kind) => {
    const state = creationRefresh();
    const identity = creationIdentity(state);
    const source = {
      prepared: prepareRequest("operation.execute", {
        kind: "CASE",
        operationId: firstId,
        target: { kind: "REFERENCE", caseReference: "OWN-REF" },
        expectedRevision: 0,
        act: { kind: "OPEN", values: caseFields.registration },
      }),
    };
    const review = {
      view: { regions: [] },
      presentation: { changes: [], unchanged: [] },
      subject: {
        kind: "CASE" as const,
        before: null,
        after: {
          caseId: firstId,
          caseReference: "OWN-REF",
          revision: 1,
          state: { kind: "ACTIVE" as const, fields: { ...caseFields, caseReference: "OWN-REF" } },
        },
        context: { ...workBasis, productVersion: "synthetic" },
      },
    };
    let operation: OperationPhase;
    switch (kind) {
      case "PREVIEWING":
        operation = { kind, source, effect: identity };
        break;
      case "REVIEWING":
        operation = { kind, source, review, confirmed: false };
        break;
      case "PREPARING":
      case "SUBMITTING":
        operation = { kind, source, effect: identity, review };
        break;
      case "UNRESOLVED":
        operation = { kind, source, effect: null };
        break;
      case "ACCEPTED":
        operation = { kind, source, receipt: null };
        break;
    }
    const active = { ...state, dateEntries: {}, operation };
    expect(
      operationReducer(active, {
        type: "RESTORE_CREATION_DRAFT",
        identity,
        editing: active.operation,
        disclosureRevision: active.disclosureRevision,
      }),
    ).toBe(active);
  },
);
it("acceptance clears transient segment buffers without adding them to its frozen request", () => {
  const active = submitted();
  const state = {
    ...active,
    dateEntries: {
      "inactive-date-field": { canonical: "", parts: [{ type: "day" as const, value: 12 }] },
    },
  };
  const recorded = operationReducer(state, {
    type: "RECORD",
    identity: effect("execute"),
    receipt: {
      view: { regions: [] },
      operationId: firstId,
      position: { sequence: 8, entryHash: "a".repeat(64) },
      recordedAt: workBasis.observedAt,
      recordedBy: actorRefInput(),
      basis: workBasis,
      subject: {
        kind: "CASE",
        caseId: "20000000-0000-4000-8000-000000000001",
        caseReference: "CASE-SERIALIZATION",
        revision: 5,
        act: "CLOSE",
        snapshot: { kind: "WITHHELD" },
      },
    },
  });
  expect(operationView(recorded).phase).toBe("ACCEPTED");
  expect(recorded.dateEntries).toEqual({});
  expect(operationView(recorded).prepared).toBe(operationView(active).prepared);
  expect(operationView(recorded).prepared!.bytes).not.toContain("inactive-date-field");
});

it.each(["read-failed", "reset"] as const)(
  "rejects a successful context proof superseded by %s even if the current editing phase is supplied",
  (kind) => {
    const state = creationRefresh();
    const observedRevision = state.disclosureRevision;
    const identity = creationIdentity(state);
    const after =
      kind === "reset"
        ? operationReducer(state, { type: "RESET", state, abandon: false })
        : operationReducer(
            { ...state, disclosureRead: identity },
            {
              type: "DISCLOSURE_READ_FAILED",
              identity,
              notice: { kind: "local", reason: "unreachable" },
              refused: false,
            },
          );
    expect(after.disclosureRevision).toBe(observedRevision + 1);
    expect(
      operationReducer(after, {
        type: "RESTORE_CREATION_DRAFT",
        identity,
        editing: after.operation,
        disclosureRevision: observedRevision,
      }),
    ).toBe(after);
  },
);
