import { prepareRequest } from "../../../src/protocol/preparedRequest";
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
  effect,
} from "../../support/operation-identity.fixtures";
import { closeReview, closeReceipt } from "../../support/operation-documents.fixtures";
const submitted = (state = reviewed(), id = "execute") =>
  operationReducer(
    operationReducer(state, {
      type: "EXECUTE",
      identity: effect(id),
    }),
    {
      type: "HANDOFF",
      identity: effect(id),
    },
  );
it("edits invalidate exposed preparation without putting executable identity in authored values", () => {
  const untouched = initial();
  const changed = operationReducer(refused(), {
    type: "EDIT",
    path: "field:reason",
    value: "B",
  });
  expect(changed.draft.values).toEqual({
    "field:reason": "B",
  });
  expect(changed.draft.binding).toEqual(untouched.draft.binding);
  expect(changed.draft.generation).toBe(untouched.draft.generation + 1);
  expect(operationView(changed).prepared).toBeNull();
  expect(
    operationReducer(changed, {
      type: "EDIT",
      path: "field:reason",
      value: "B",
    }),
  ).toBe(changed);
});
it("uncertainty survives later definite failures and blocks edits while allowing exact retry", () => {
  const submitting = submitted();
  const unknown = operationReducer(submitting, {
    type: "REFUSE",
    notice: {
      kind: "local",
      reason: "outcomeUnknown",
    },
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
      path: "field:caseReference",
      value: "OTHER",
    }),
  ).toBe(rejected);
  expect(
    operationView(
      operationReducer(rejected, {
        type: "EXECUTE",
        identity: effect("execute"),
      }),
    ).phase,
  ).toBe("UNRESOLVED");
});
it("requires an exact review and consent and blocks edits while an attempt is pending", () => {
  expect(
    operationReducer(initial(), {
      type: "EXECUTE",
      identity: effect("execute"),
    }),
  ).toEqual(initial());
  expect(
    operationView(
      operationReducer(begin(), {
        type: "CONFIRM",
        value: true,
      }),
    ).confirmed,
  ).toBe(false);
  const original = reviewed();
  const pending = operationReducer(original, {
    type: "EXECUTE",
    identity: effect("execute"),
  });
  expect(operationView(pending).prepared).toBe(operationView(original).prepared);
  expect(
    operationReducer(pending, {
      type: "EDIT",
      path: "field:caseReference",
      value: "OTHER",
    }),
  ).toBe(pending);
});
const unknown = () =>
  operationReducer(submitted(), {
    type: "REFUSE",
    identity: effect("execute"),
    notice: {
      kind: "local",
      reason: "outcomeUnknown",
    },
    target: null,
    uncertain: true,
    disclosure: false,
  });
it("settles original acceptance after epoch or target change while keeping disclosure masked", () => {
  const submitting = submitted();
  for (const context of [
    {
      credential: "new-epoch",
      target: submitting.draft.target,
    },
    {
      credential: "token",
      target: "CASE:OTHER",
    },
  ]) {
    const invalidated = operationReducer(submitting, {
      type: "CONTEXT",
      epoch: 0,
      ...context,
      owner: {
        ...effect().owner,
        sessionEpoch: 0,
      },
    });
    expect(invalidated.delivery.phase.kind).toBe("SUBMITTING");
    expect(
      operationReducer(invalidated, {
        type: "EDIT",
        path: "field:reason",
        value: "changed",
      }),
    ).toBe(invalidated);
    const late = operationReducer(invalidated, {
      type: "RECORD",
      receipt: closeReceipt,
      identity: effect("execute"),
    });
    expect(late.delivery.phase.kind).toBe("ACCEPTED");
    expect(late.outbox).toBe(invalidated.outbox);
    expect(operationView(late).prepared).toBe(operationView(submitting).prepared);
    expect(operationView(late).receipt).toBeNull();
  }
});
it("disclosure invalidation removes a submitting review and its late completion cannot restore it", () => {
  const pending = submitted();
  expect(operationView(pending).review).not.toBeNull();
  const invalid = operationReducer(pending, {
    type: "INVALIDATE",
    refused: true,
  });
  expect(operationView(invalid).review).toBeNull();
  expect(operationView(invalid).phase).toBe("SUBMITTING");
  expect(
    operationReducer(invalid, {
      type: "EDIT",
      path: "field:reason",
      value: "changed",
    }),
  ).toBe(invalid);
  const late = operationReducer(invalid, {
    type: "RECORD",
    identity: effect("execute"),
    receipt: closeReceipt,
  });
  expect(late.delivery.phase.kind).toBe("ACCEPTED");
  expect(operationView(late).receipt).toBeNull();
  expect(operationView(late).review).toBeNull();
});
const controlEvents = (state: OperationState): OperationAction[] => [
  {
    type: "EDIT",
    path: "field:caseReference",
    value: "OTHER",
  },
  {
    type: "RETURN",
  },
  {
    type: "CONFIRM",
    value: true,
  },
  {
    type: "REFUSE",
    notice: {
      kind: "local",
      reason: "incomplete",
    },
    target: null,
    uncertain: false,
    disclosure: false,
  },
  {
    type: "INVALIDATE",
    refused: true,
  },
  {
    type: "RESTORE",
  },
  {
    type: "RESET",
    state: initial(),
    abandon: false,
  },
  {
    type: "CONTEXT",
    epoch: 0,
    credential: state.disclosed.context.credential,
    target: state.disclosed.context.target,
    owner: {
      ...effect().owner,
      sessionEpoch: 0,
    },
  },
];
it("200 deterministic event sequences preserve prior uncertainty and cannot schedule cleanup", () => {
  for (let seed = 1; seed <= 200; seed += 1) {
    let state = unknown();
    const { prepared } = operationView(state);
    const { outbox: effects } = state;
    let cursor = seed;
    for (let step = 0; step < 20; step += 1) {
      cursor = (Math.imul(cursor, 1664525) + 1013904223) >>> 0;
      const events = controlEvents(state);
      state = operationReducer(state, events[cursor % events.length]!);
      expect(operationView(state).priorUncertain).toBe(true);
      expect(operationView(state).prepared).toBe(prepared);
      expect(state.outbox.every((value) => effects.includes(value))).toBe(true);
    }
  }
});
it("duplicate execute events emit one ledger effect and mismatched effect IDs cannot complete it", () => {
  const first = operationReducer(reviewed(), {
    type: "EXECUTE",
    identity: effect("first"),
  });
  const duplicate = operationReducer(first, {
    type: "EXECUTE",
    identity: effect("second"),
  });
  expect(duplicate).toBe(first);
  expect(first.outbox.filter((value) => value.kind === "EXECUTE")).toHaveLength(1);
  expect(
    operationReducer(first, {
      type: "REFUSE",
      identity: effect("second"),
      notice: {
        kind: "local",
        reason: "incomplete",
      },
      target: null,
      uncertain: false,
      disclosure: false,
    }),
  ).toBe(first);
});
it("keeps refused disclosure masked after transient read and conflict failures until a positive read", () => {
  const identity = effect("reread");
  const state = {
    ...initial(),
    disclosed: {
      ...initial().disclosed,
      refused: true,
      read: {
        resource: "case.get" as const,
        identity,
        query: {
          target: {
            kind: "ID" as const,
            caseId: initial().disclosed.caseObservation!.context.caseId,
          },
        },
      },
    },
  };
  const failed = operationReducer(state, {
    type: "DISCLOSURE_READ_FAILED",
    identity,
    notice: {
      kind: "local",
      reason: "unreachable",
    },
    refused: false,
  });
  expect(failed.disclosed.refused).toBe(true);
  expect(failed.disclosed.revision).toBe(state.disclosed.revision + 1);
  expect(
    operationReducer(failed, {
      type: "INVALIDATE",
      refused: false,
    }).disclosed.refused,
  ).toBe(true);
  const restored = operationReducer(
    {
      ...failed,
      disclosed: {
        ...failed.disclosed,
        read: {
          resource: "case.get" as const,
          identity,
          query: {
            target: {
              kind: "ID" as const,
              caseId: initial().disclosed.caseObservation!.context.caseId,
            },
          },
        },
      },
    },
    {
      type: "DISCLOSED",
      identity,
      value: initial().disclosed.caseObservation!,
    },
  );
  expect(restored.disclosed.refused).toBe(false);
  const conflict = operationReducer(begin(), {
    type: "REFUSE",
    identity: effect(),
    notice: {
      kind: "local",
      reason: "incomplete",
    },
    rejection: {
      kind: "CONFLICT",
      advice: {
        message: {
          messageId: "diagnostic.REVISION_CONFLICT",
          arguments: [],
        },
        action: "REVIEW_LATEST",
      },
      actualRevision: 5,
      document: closeReview.document,
    },
    uncertain: false,
    disclosure: false,
    target: null,
  });
  expect(conflict.disclosed.conflict).not.toBeNull();
  const conflictFailed = operationReducer(
    {
      ...conflict,
      disclosed: {
        ...conflict.disclosed,
        refused: true,
        read: {
          resource: "conflict" as const,
          identity,
          query: conflict.disclosed.conflict!.source,
        },
      },
    },
    {
      type: "CONFLICT_READ_FAILED",
      identity,
      conflictId: conflict.disclosed.conflict!.identity.id,
      notice: {
        kind: "local",
        reason: "unreachable",
      },
      refused: false,
    },
  );
  expect(conflictFailed.disclosed.refused).toBe(true);
  expect(conflictFailed.disclosed.revision).toBe(conflict.disclosed.revision + 1);
});
it("discarding authored intent cannot restore a refused disclosure without a positive observation", () => {
  const initialState = initial();
  const refusedState = {
    ...initialState,
    disclosed: {
      ...initialState.disclosed,
      refused: true,
    },
  };
  const resetState = operationReducer(refusedState, {
    type: "RESET",
    state: initial(),
    abandon: false,
  });
  expect(resetState.disclosed.refused).toBe(true);
  expect(resetState.disclosed.revision).toBe(refusedState.disclosed.revision + 1);
  expect(resetState.disclosed.confirmedContext).toBe(refusedState.disclosed.confirmedContext);
  const identity = effect("explicit-current-read");
  const restored = operationReducer(
    {
      ...resetState,
      disclosed: {
        ...resetState.disclosed,
        read: {
          resource: "case.get" as const,
          identity,
          query: {
            target: {
              kind: "ID" as const,
              caseId: initial().disclosed.caseObservation!.context.caseId,
            },
          },
        },
      },
    },
    {
      type: "DISCLOSED",
      identity,
      value: initialState.disclosed.caseObservation!,
    },
  );
  expect(restored.disclosed.refused).toBe(false);
});
const creationRefresh = (): OperationState => {
  const state = initial();
  const prior = {
    ...state.disclosed.context,
    credential: "token",
    epoch: 0,
    target: "CASE:verified-owner:NEW",
  };
  return {
    ...state,
    draft: {
      ...state.draft,
      entry: {
        subject: "CASE",
        kind: "case.open",
      },
      target: prior.target,
      binding: {
        kind: "CASE",
        target: { kind: "REFERENCE", caseReference: "" },
        expectedRevision: 0,
      },
      dateEntries: {
        "field:incidentDate": {
          canonical: "",
          parts: [
            {
              type: "day",
              value: 12,
            },
          ],
        },
      },
    },
    disclosed: {
      ...state.disclosed,
      context: {
        ...prior,
        credential: "token",
        epoch: 0,
        generation: 1,
      },
      confirmedContext: prior,
      refused: true,
      caseObservation: null,
    },
  };
};
const creationIdentity = (state: OperationState) => ({
  owner: effect().owner,
  id: "current-context-restore",
  contextGeneration: state.disclosed.context.generation,
  credential: state.disclosed.context.credential,
  target: state.disclosed.context.target,
});
it("restores only owned creation entry after a fenced epoch refresh without restoring a review or case facts", () => {
  const state = creationRefresh();
  const restored = operationReducer(state, {
    type: "RESTORE_CREATION_DRAFT",
    editing: state.delivery.phase,
    disclosureRevision: state.disclosed.revision,
    identity: creationIdentity(state),
  });
  expect(restored.disclosed.confirmedContext).toBe(state.disclosed.context);
  expect(restored.disclosed.refused).toBe(false);
  expect(restored.draft.values).toBe(state.draft.values);
  expect(restored.draft.dateEntries).toBe(state.draft.dateEntries);
  expect(restored.disclosed.caseObservation).toBeNull();
  expect(operationView(restored).review).toBeNull();
  expect(restored.outbox).toEqual([]);
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
  const observedRevision = state.disclosed.revision;
  if (kind === "same-epoch" || kind === "credential-only") {
    state = operationReducer(state, {
      type: "INVALIDATE",
      refused: true,
    });
  }
  if (kind === "credential-only") {
    state = {
      ...state,
      disclosed: {
        ...state.disclosed,
        context: {
          ...state.disclosed.context,
          epoch: state.disclosed.confirmedContext.epoch,
        },
      },
    };
  }
  const editing = state.delivery.phase;
  if (kind === "intervening-refusal") {
    state = operationReducer(state, {
      type: "INVALIDATE",
      refused: true,
    });
  }
  if (kind === "same-epoch") {
    state = {
      ...state,
      disclosed: {
        ...state.disclosed,
        context: {
          ...state.disclosed.context,
          credential: state.disclosed.confirmedContext.credential,
          epoch: state.disclosed.confirmedContext.epoch,
        },
      },
    };
  }
  if (kind === "different-target") {
    state = {
      ...state,
      disclosed: {
        ...state.disclosed,
        context: {
          ...state.disclosed.context,
          target: "CASE:different-owner:NEW",
        },
      },
    };
  }
  if (kind === "existing-case") {
    state = {
      ...state,
      disclosed: {
        ...state.disclosed,
        caseObservation: initial().disclosed.caseObservation,
      },
    };
  }
  if (kind === "other-act") {
    state = {
      ...state,
      draft: {
        ...state.draft,
        entry: {
          subject: "CASE",
          kind: "case.close",
        },
      },
    };
  }
  const identity = creationIdentity(state);
  const restored = operationReducer(state, {
    type: "RESTORE_CREATION_DRAFT",
    editing,
    disclosureRevision: observedRevision,
    identity:
      kind === "stale-identity"
        ? {
            ...identity,
            contextGeneration: identity.contextGeneration - 1,
          }
        : identity,
  });
  expect(restored).toBe(state);
  expect(restored.disclosed.refused).toBe(true);
});
it("keeps date entry ownership through unrelated edits and clears only omitted or reset UI state", () => {
  const state = {
    ...initial(),
    draft: {
      ...initial().draft,
      dateEntries: {
        "field:incidentDate": {
          canonical: "",
          parts: [
            {
              type: "day" as const,
              value: 12,
            },
          ],
        },
        "field:incidentNotificationDate": {
          canonical: "",
          parts: [
            {
              type: "month" as const,
              value: 10,
            },
          ],
        },
      },
    },
  };
  const edited = operationReducer(state, {
    type: "EDIT",
    path: "field:claimantName",
    value: "Authored name",
  });
  expect(edited.draft.dateEntries).toBe(state.draft.dateEntries);
  const omitted = operationReducer(edited, {
    type: "EDIT_FIELDS",
    values: {},
    omitted: ["field:incidentDate"],
  });
  expect(omitted.draft.dateEntries).toEqual({
    "field:incidentNotificationDate": {
      canonical: "",
      parts: [
        {
          type: "month",
          value: 10,
        },
      ],
    },
  });
  expect(
    operationReducer(omitted, {
      type: "RESET",
      state: initial(),
      abandon: false,
    }).draft.dateEntries,
  ).toEqual({});
  expect(
    operationReducer(state, {
      type: "CLEAR_DATE_ENTRIES",
    }).draft.dateEntries,
  ).toEqual({});
});
it("a partial date replaces a stale canonical value without adding network effects and is fenced after disclosure loss", () => {
  const path = "field:incidentDate";
  const state = {
    ...initial(),
    draft: {
      ...initial().draft,
      values: {
        ...initial().draft.values,
        [path]: "2024-02-29",
      },
    },
  };
  const action: OperationAction = {
    type: "DATE_ENTRY",
    path,
    canonical: "2024-02-29",
    parts: [
      {
        type: "month",
        value: 2,
      },
      {
        type: "day",
        value: 29,
      },
    ],
  };
  const partial = operationReducer(state, action);
  expect(partial.draft.values[path]).toBe("");
  expect(partial.draft.dateEntries[path]).toEqual({
    canonical: "",
    parts: [
      {
        type: "month",
        value: 2,
      },
      {
        type: "day",
        value: 29,
      },
    ],
  });
  expect(partial.outbox).toBe(state.outbox);
  expect(
    operationReducer(
      {
        ...state,
        disclosed: {
          ...state.disclosed,
          refused: true,
        },
      },
      action,
    ).draft.dateEntries,
  ).toEqual({});
  expect(
    operationReducer(state, {
      ...action,
      canonical: "stale-value",
    }),
  ).toBe(state);
});
it.each(["PREVIEWING", "REVIEWING", "PREPARING", "SUBMITTING", "UNRESOLVED", "ACCEPTED"] as const)(
  "does not restore editable creation entry while %s",
  (kind) => {
    const state = creationRefresh();
    const identity = creationIdentity(state);
    const source = {
      prepared: prepareRequest("operation.execute", {
        operationId: firstId,
        task: "case.open",
        binding: {
          kind: "CASE",
          target: {
            kind: "REFERENCE",
            caseReference: "OWN-REF",
          },
          expectedRevision: 0,
        },
        inputs: {
          "field:incidentDate": "2026-09-01",
          "field:incidentNotificationDate": "2026-09-02",
          "field:incidentCountry": "LV",
          "field:claimantName": "Synthetic claimant",
          "field:insurerName": "Synthetic insurer",
          "field:claimedAmount": "12.34",
          "field:claimedCurrency": "EUR",
        },
      }),
      submittedRows: {},
    };
    const review: typeof closeReview = {
      ...closeReview,
      document: {
        ...closeReview.document,
        context: {
          ...closeReview.document.context,
          task: "case.open",
          target: {
            kind: "CASE",
            caseId: firstId,
            beforeRevision: null,
          },
        },
      },
    };
    let operation: OperationPhase;
    switch (kind) {
      case "PREVIEWING":
        operation = {
          kind,
          source,
          effect: identity,
        };
        break;
      case "REVIEWING":
        operation = {
          kind,
          source,
          reviewIdentity: identity,
          confirmed: false,
        };
        break;
      case "PREPARING":
      case "SUBMITTING":
        operation = {
          kind,
          source,
          effect: identity,
          reviewIdentity: identity,
        };
        break;
      case "UNRESOLVED":
        operation = {
          kind,
          source,
          pending: null,
        };
        break;
      case "ACCEPTED":
        operation = {
          kind,
          source,
        };
        break;
    }
    const active = {
      ...state,
      disclosed: { ...state.disclosed, review: { identity, value: review } },
      draft: {
        ...state.draft,
        dateEntries: {},
      },
      delivery: {
        ...state.delivery,
        phase: operation,
      },
    };
    expect(
      operationReducer(active, {
        type: "RESTORE_CREATION_DRAFT",
        identity,
        editing: active.delivery.phase,
        disclosureRevision: active.disclosed.revision,
      }),
    ).toBe(active);
  },
);
it("acceptance clears transient segment buffers without adding them to its frozen request", () => {
  const active = submitted();
  const state = {
    ...active,
    draft: {
      ...active.draft,
      dateEntries: {
        "inactive-date-field": {
          canonical: "",
          parts: [
            {
              type: "day" as const,
              value: 12,
            },
          ],
        },
      },
    },
  };
  const recorded = operationReducer(state, {
    type: "RECORD",
    identity: effect("execute"),
    receipt: closeReceipt,
  });
  expect(operationView(recorded).phase).toBe("ACCEPTED");
  expect(recorded.draft.dateEntries).toEqual({});
  expect(operationView(recorded).prepared).toBe(operationView(active).prepared);
  expect(operationView(recorded).prepared!.bytes).not.toContain("inactive-date-field");
});
it.each(["read-failed", "reset"] as const)(
  "rejects a successful context proof superseded by %s even if the current editing phase is supplied",
  (kind) => {
    const state = creationRefresh();
    const observedRevision = state.disclosed.revision;
    const identity = creationIdentity(state);
    const after =
      kind === "reset"
        ? operationReducer(state, {
            type: "RESET",
            state,
            abandon: false,
          })
        : operationReducer(
            {
              ...state,
              disclosed: {
                ...state.disclosed,
                read: {
                  resource: "case.get" as const,
                  identity,
                  query: {
                    target: {
                      kind: "ID" as const,
                      caseId: initial().disclosed.caseObservation!.context.caseId,
                    },
                  },
                },
              },
            },
            {
              type: "DISCLOSURE_READ_FAILED",
              identity,
              notice: {
                kind: "local",
                reason: "unreachable",
              },
              refused: false,
            },
          );
    expect(after.disclosed.revision).toBe(observedRevision + 1);
    expect(
      operationReducer(after, {
        type: "RESTORE_CREATION_DRAFT",
        identity,
        editing: after.delivery.phase,
        disclosureRevision: observedRevision,
      }),
    ).toBe(after);
  },
);

it.each(["serviceOrigin", "installationId", "actorId", "sessionEpoch"] as const)(
  "fences %s independently of resource identity and settles only the original delivery",
  (field) => {
    const pending = submitted();
    const identity = effect("execute");
    const owner = { ...identity.owner, [field]: field === "sessionEpoch" ? 1 : "different" };
    const changed = operationReducer(pending, {
      type: "CONTEXT",
      owner,
      epoch: owner.sessionEpoch,
      credential: identity.credential,
      target: identity.target,
    });
    expect(changed.delivery.owner).toEqual(identity.owner);
    expect(changed.draft.owner).toEqual(identity.owner);
    expect(changed.draft.values).toBe(pending.draft.values);
    expect(changed.disclosed.review).toBeNull();
    expect(changed.disclosed.receipt).toBeNull();
    expect(
      operationReducer(changed, {
        type: "RECORD",
        identity: { ...identity, owner },
        receipt: closeReceipt,
      }),
    ).toBe(changed);
    const late = operationReducer(changed, { type: "RECORD", identity, receipt: closeReceipt });
    expect(late.delivery.phase.kind).toBe("ACCEPTED");
    expect(late.disclosed.receipt).toBeNull();
    expect(late.outbox).toHaveLength(0);
  },
);
it("rejects a current read whose document belongs to another case", () => {
  const state = initial();
  const identity = effect("case-read");
  const reading = operationReducer(state, {
    type: "READ_CURRENT",
    identity,
    caseId: "requested-case",
    reference: "CASE-1",
  });
  expect(reading.disclosed.read).toEqual({
    resource: "case.get",
    identity,
    query: { target: { kind: "ID", caseId: "requested-case" } },
  });
  const wrong = operationReducer(reading, {
    type: "DISCLOSED",
    identity,
    value: state.disclosed.caseObservation!,
  });
  expect(wrong.disclosed.confirmedContext).toBe(state.disclosed.confirmedContext);
  expect(wrong.disclosed.caseObservation).toBe(state.disclosed.caseObservation);
  expect(wrong.disclosed.revision).toBe(state.disclosed.revision + 1);
  expect(wrong.disclosed.notice).toEqual({ kind: "local", reason: "incomplete" });
});
