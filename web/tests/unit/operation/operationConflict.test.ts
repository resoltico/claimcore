import { expect, it } from "vitest";
import { operationReducer, operationView } from "../../../src/operation/operationState";
import { rebasedTaskBinding } from "../../../src/operation/operationConflict";
import { prepareRequest } from "../../../src/protocol/preparedRequest";
import { begin, effect, reviewed, secondId } from "../../support/operation-identity.fixtures";
import { current } from "../../support/operation-form.fixtures";
import type { HumanRejection_CONFLICT } from "@contracts/client";
const diagnostic: HumanRejection_CONFLICT = {
  kind: "CONFLICT",
  actualRevision: 5,
  advice: {
    message: {
      messageId: "diagnostic.REVISION_CONFLICT",
      arguments: [],
    },
    action: "REVIEW_LATEST",
  },
  document: {
    screen: {
      regions: [],
    },
    context: {
      kind: "PAGE",
      tasks: [],
      nextCursor: null,
    },
    basis: current.basis,
    disclosure: "WITHHELD",
  },
};
const conflicted = () =>
  operationReducer(begin(), {
    type: "REFUSE",
    identity: effect(),
    notice: {
      kind: "human",
      advice: diagnostic.advice,
    },
    rejection: diagnostic,
    target: null,
    uncertain: false,
    disclosure: false,
  });
it("rebinds the declared revision without putting executable identity or authoring values in the binding", () => {
  const command = prepareRequest("operation.execute", {
    kind: "CASE",
    operationId: secondId,
    target: {
      kind: "REFERENCE",
      caseReference: "CASE-SERIALIZATION",
    },
    expectedRevision: 4,
    act: {
      kind: "AMEND_REGISTRATION",
      values: {
        claimedAmount: "1234.56",
        claimedCurrency: "EUR",
      },
    },
  });
  const { bytes } = command;
  const binding = rebasedTaskBinding(command.input, 8);
  expect(binding).toEqual({
    kind: "CASE",
    target: {
      kind: "REFERENCE",
      caseReference: "CASE-SERIALIZATION",
    },
    revision: 8,
  });
  expect(command.bytes).toBe(bytes);
  expect(command.input).toMatchObject({
    expectedRevision: 4,
    act: {
      values: {
        claimedAmount: "1234.56",
        claimedCurrency: "EUR",
      },
    },
  });
});
it("does not rebase an uncertain request when its exact retry returns a revision conflict", () => {
  let state = operationReducer(reviewed(), {
    type: "EXECUTE",
    identity: effect("send"),
  });
  state = operationReducer(state, {
    type: "HANDOFF",
    identity: effect("send"),
  });
  state = operationReducer(state, {
    type: "REFUSE",
    identity: effect("send"),
    notice: {
      kind: "local",
      reason: "outcomeUnknown",
    },
    target: null,
    uncertain: true,
    disclosure: false,
  });
  const { bytes } = operationView(state).prepared!;
  state = operationReducer(state, {
    type: "EXECUTE",
    identity: effect("retry"),
  });
  state = operationReducer(state, {
    type: "REFUSE",
    identity: effect("retry"),
    notice: {
      kind: "human",
      advice: diagnostic.advice,
    },
    rejection: diagnostic,
    target: null,
    uncertain: false,
    disclosure: false,
  });
  expect(state.disclosed.conflict).toBeNull();
  expect(operationView(state).phase).toBe("UNRESOLVED");
  expect(operationView(state).prepared!.bytes).toBe(bytes);
  expect(state.outbox.some((entry) => entry.kind === "READ_CONFLICT")).toBe(false);
});
it("fences refreshed conflict observations by correlation identity and session", () => {
  const state = conflicted();
  expect(state.disclosed.conflict).not.toBeNull();
  const snapshot = state.disclosed.conflict!;
  const value = {
    current: {
      ...current,
      context: {
        ...current.context,
        revision: 8,
      },
    },
    revision: 8,
  };
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
    target: state.draft.target,
    owner: {
      ...effect().owner,
      sessionEpoch: 1,
    },
  });
  expect(replacement.disclosed.conflict).toBeNull();
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
  const snapshot = state.disclosed.conflict!;
  const observed = operationReducer(state, {
    type: "CONFLICT_OBSERVED",
    identity: effect(),
    conflictId: snapshot.identity.id,
    value: {
      current: {
        ...current,
        context: {
          ...current.context,
          revision: 8,
        },
      },
      revision: 8,
    },
  });
  const prepared = prepareRequest("operation.execute", {
    kind: "CASE",
    operationId: secondId,
    target: {
      kind: "REFERENCE",
      caseReference: "OTHER-CASE",
    },
    expectedRevision: 8,
    act: {
      kind: "CLOSE",
    },
  });
  expect(
    operationReducer(observed, {
      type: "REVIEW_LATEST",
      identity: effect("new-review"),
      conflictId: snapshot.identity.id,
      generation: observed.draft.generation,
      values: observed.draft.values,
      source: {
        prepared,
        submittedRows: {},
      },
    }),
  ).toBe(observed);
  expect(
    operationReducer(observed, {
      type: "PREVIEW",
      identity: effect("implicit"),
      generation: observed.draft.generation,
      source: {
        prepared,
        submittedRows: {},
      },
    }),
  ).toBe(observed);
});
