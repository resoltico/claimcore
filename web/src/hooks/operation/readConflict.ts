import { requestEndpoint, resultNotice } from "../../api/service";
import { isDisclosureRefused } from "../../api/outcomes";
import type {
  OperationAction,
  OperationEffect,
  ConflictObservation,
} from "../../operation/operationTypes";
type Effect = Extract<OperationEffect, { kind: "READ_CONFLICT" }>;
type Result =
  | { kind: "observed"; value: ConflictObservation }
  | {
      kind: "failed";
      notice: Extract<OperationAction, { type: "CONFLICT_READ_FAILED" }>["notice"];
      refused: boolean;
    };
const observe = async (
  effect: Effect,
  current: ConflictObservation["current"],
  signal: AbortSignal,
): Promise<Result> => {
  if (current.disclosure === "ERASED") {
    return { kind: "observed", value: { current, revision: null } };
  }
  const { input } = effect.source.prepared;
  let observedRevision = current.context.revision;
  if (input.kind === "WORK") {
    observedRevision = current.context.workRevision;
  }
  if (input.kind === "REMINDER") {
    if (input.act.kind === "CREATE") {
      return { kind: "failed", notice: { kind: "local", reason: "incomplete" }, refused: false };
    }
    const result = await requestEndpoint(
      "reminder.get",
      effect.identity.credential,
      { target: input.target, reminderId: input.act.reminderId },
      signal,
    );
    if (result.kind !== "outcome" || result.value.outcome.kind !== "SUCCEEDED") {
      return { kind: "failed", notice: resultNotice(result), refused: isDisclosureRefused(result) };
    }
    observedRevision = result.value.outcome.document.context.revision;
  }
  return observedRevision < effect.minimumRevision
    ? { kind: "failed", notice: { kind: "local", reason: "incomplete" }, refused: false }
    : { kind: "observed", value: { current, revision: observedRevision } };
};
export const readConflict = async (
  effect: Effect,
  reads: Map<string, AbortController>,
): Promise<OperationAction> => {
  const controller = new AbortController();
  reads.set(effect.identity.id, controller);
  const common = { identity: effect.identity, conflictId: effect.conflictId };
  try {
    const result = await requestEndpoint(
      "case.get",
      effect.identity.credential,
      { target: effect.source.prepared.input.target },
      controller.signal,
    );
    if (result.kind !== "outcome" || result.value.outcome.kind !== "SUCCEEDED") {
      return {
        type: "CONFLICT_READ_FAILED",
        ...common,
        notice: resultNotice(result),
        refused: isDisclosureRefused(result),
      };
    }
    const observation = await observe(effect, result.value.outcome.document, controller.signal);
    return observation.kind === "observed"
      ? { type: "CONFLICT_OBSERVED", ...common, value: observation.value }
      : {
          type: "CONFLICT_READ_FAILED",
          ...common,
          notice: observation.notice,
          refused: observation.refused,
        };
  } finally {
    reads.delete(effect.identity.id);
  }
};
