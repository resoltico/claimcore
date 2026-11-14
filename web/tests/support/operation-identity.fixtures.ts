import { testWorkspaceIdentity } from "./presentation-test-support";
const owner = {
  serviceOrigin: location.origin,
  installationId: testWorkspaceIdentity.installationId,
  actorId: testWorkspaceIdentity.actorId,
  sessionEpoch: 0,
};
import type { OperationState, EffectIdentity } from "../../src/operation/operationState";
import { operationReducer } from "../../src/operation/operationState";
import { valuesFor, preparedFor, current } from "./operation-form.fixtures";
import { closeReview } from "./operation-documents.fixtures";
export { firstId, secondId } from "./operation-form.fixtures";
export const effect = (id = "preview"): EffectIdentity => ({
  owner,
  id,
  contextGeneration: 0,
  credential: "token",
  target: "CASE:CASE-SERIALIZATION",
});
export const initial = (): OperationState => {
  const context = {
    generation: 0,
    owner,
    epoch: 0,
    credential: "token",
    target: "CASE:CASE-SERIALIZATION",
  };
  return {
    outbox: [],
    draft: {
      owner,
      entry: {
        subject: "CASE",
        kind: "case.close",
      },
      target: "CASE:CASE-SERIALIZATION",
      binding: {
        kind: "CASE",
        target: {
          kind: "REFERENCE",
          caseReference: "CASE-SERIALIZATION",
        },
        expectedRevision: 4,
      },
      dateEntries: {},
      values: valuesFor(),
      baseline: valuesFor(),
      generation: 0,
      pendingDiscard: null,
    },
    disclosed: {
      review: null,
      receipt: null,
      context,
      confirmedContext: context,
      caseObservation: current,
      rejection: null,
      read: null,
      conflict: null,
      refused: false,
      revision: 0,
      notice: null,
      fieldErrors: {},
    },
    delivery: {
      notice: null,
      owner,
      credential: "token",
      phase: {
        kind: "EDITING",
        exposed: null,
      },
    },
  };
};
export const begin = (state = initial()) =>
  operationReducer(state, {
    type: "PREVIEW",
    source: {
      prepared: preparedFor(),
      submittedRows: {},
    },
    identity: effect(),
    generation: state.draft.generation,
  });
export const refused = (state = begin()) =>
  operationReducer(state, {
    type: "REFUSE",
    notice: {
      kind: "local",
      reason: "unreachable",
    },
    target: null,
    uncertain: false,
    disclosure: false,
  });
export const reviewed = () =>
  operationReducer(
    operationReducer(begin(), {
      type: "REVIEW",
      review: closeReview,
      identity: effect(),
    }),
    {
      type: "CONFIRM",
      value: true,
    },
  );
