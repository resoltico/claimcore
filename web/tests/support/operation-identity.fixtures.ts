import type { OperationState, EffectIdentity } from "../../src/operation/operationState";
import { operationReducer } from "../../src/operation/operationState";
import { valuesFor, preparedFor } from "./operation-form.fixtures";
import { review } from "./service-foundation.fixtures";
export { firstId, secondId } from "./operation-form.fixtures";
export const effect = (id = "preview"): EffectIdentity => ({
  id,
  token: "token",
  target: "CASE:CASE-SERIALIZATION",
});
export const initial = (): OperationState => ({
  target: "CASE:CASE-SERIALIZATION",
  context: { token: "token", target: "CASE:CASE-SERIALIZATION" },
  disclosureToken: "token",
  disclosureRefused: false,
  values: valuesFor(),
  baseline: valuesFor(),
  generation: 0,
  operation: { kind: "EDITING", exposed: null },
  effects: [],
  notice: null,
  fieldError: null,
  pendingDiscard: null,
});
export const begin = (state = initial()) =>
  operationReducer(state, {
    type: "PREVIEW",
    prepared: preparedFor(),
    identity: effect(),
    generation: state.generation,
  });
export const refused = (state = begin()) =>
  operationReducer(state, {
    type: "REFUSE",
    notice: { kind: "local", reason: "unreachable" },
    target: null,
    uncertain: false,
    disclosure: false,
  });
export const reviewed = () =>
  operationReducer(operationReducer(begin(), { type: "REVIEW", review, identity: effect() }), {
    type: "CONFIRM",
    value: true,
  });
