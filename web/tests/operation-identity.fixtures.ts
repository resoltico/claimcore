import type { OperationState } from "../src/interaction/operationState";
import { operationReducer } from "../src/interaction/operationState";
import { valuesFor, preparedFor } from "./operation-form.fixtures";
import { review } from "./service-foundation.fixtures";
export { firstId, secondId } from "./operation-form.fixtures";
export const initial = (): OperationState => ({
  target: "CASE:CASE-SERIALIZATION",
  disclosureToken: "token",
  disclosureRefused: false,
  phase: "EDITING",
  values: valuesFor(),
  baseline: valuesFor(),
  prepared: null,
  priorUncertain: false,
  review: null,
  receipt: null,
  notice: null,
  fieldError: null,
  confirmed: false,
});
export const begin = (state = initial()) =>
  operationReducer(state, { type: "PREVIEW", prepared: preparedFor() });
export const refused = (state = begin()) =>
  operationReducer(state, {
    type: "REFUSE",
    notice: { kind: "local", reason: "unreachable" },
    target: null,
    uncertain: false,
    disclosure: false,
  });
export const reviewed = () =>
  operationReducer(operationReducer(begin(), { type: "REVIEW", review, token: "token" }), {
    type: "CONFIRM",
    value: true,
  });
