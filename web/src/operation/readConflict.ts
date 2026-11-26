import { apiEndpoints, requestEndpoint, resultNotice } from "../api/service";
import { hasDocumentSubject } from "../protocol/types";

import { isDisclosureRefused } from "../api/outcomes";
import type { OperationAction, OperationEffect, ConflictObservation } from "./operationTypes";
type Effect = Extract<OperationEffect, { kind: "READ_CONFLICT" }>;
type Result =
  | { kind: "observed"; value: ConflictObservation }
  | {
      kind: "failed";
      notice: Extract<OperationAction, { type: "CONFLICT_READ_FAILED" }>["notice"];
      refused: boolean;
    };
type Failed = Extract<Result, { kind: "failed" }>;
const incomplete: Failed = {
  kind: "failed",
  notice: { kind: "local", reason: "incomplete" },
  refused: false,
};

const partyRevision = (current: ConflictObservation["current"], partyId: string) =>
  current.screen.regions
    .flatMap((region) => (region.content.kind === "TABLE" ? region.content.rows : []))
    .flatMap((row) => row.actions)
    .flatMap((action) => {
      const binding = action?.binding;
      return binding?.kind === "PARTY" && "partyId" in binding && binding.partyId === partyId
        ? [binding.expectedRevision]
        : [];
    })[0];

const reminderRevision = async (
  effect: Effect,
  reminderId: string,
  signal: AbortSignal,
): Promise<number | Failed> => {
  const result = await requestEndpoint(
    apiEndpoints.reminderGet,
    effect.identity.credential,
    { target: effect.target, reminderId },
    signal,
  );
  if (result.kind !== "outcome" || result.value.outcome.kind !== "SUCCEEDED") {
    return { kind: "failed", notice: resultNotice(result), refused: isDisclosureRefused(result) };
  }
  return (
    result.value.outcome.document.actions.flatMap(({ binding }) =>
      binding.kind === "REMINDER" &&
      "reminderId" in binding &&
      binding.reminderId === reminderId &&
      binding.target.kind === "ID" &&
      binding.target.caseId === effect.target.caseId
        ? [binding.expectedRevision]
        : [],
    )[0] ?? incomplete
  );
};

const observe = async (
  effect: Effect,
  current: ConflictObservation["current"],
  signal: AbortSignal,
): Promise<Result> => {
  if (current.disclosure === "ERASED") {
    return { kind: "observed", value: { current, revision: null } };
  }
  if (current.disclosure !== "CURRENT") {
    return incomplete;
  }
  const { binding } = effect.source.prepared.input;
  let revision: number | Failed = current.subject.revision;
  if (binding.kind === "WORK") {
    revision = current.subject.workRevision;
  } else if ("partyId" in binding) {
    revision = partyRevision(current, binding.partyId) ?? incomplete;
  } else if ("reminderId" in binding) {
    revision = await reminderRevision(effect, binding.reminderId, signal);
  } else if (binding.kind === "PARTY" || binding.kind === "REMINDER") {
    return incomplete;
  }
  if (typeof revision !== "number") {
    return revision;
  }
  return revision < effect.minimumRevision
    ? incomplete
    : { kind: "observed", value: { current, revision } };
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
      apiEndpoints.caseGet,
      effect.identity.credential,
      { target: effect.target },
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
    const { document } = result.value.outcome;
    if (!hasDocumentSubject(document, "CASE") || document.subject.caseId !== effect.target.caseId) {
      return {
        type: "CONFLICT_READ_FAILED",
        ...common,
        notice: { kind: "local", reason: "incomplete" },
        refused: false,
      };
    }
    const observation = await observe(effect, document, controller.signal);
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
