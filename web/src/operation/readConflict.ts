import type { HumanDocument } from "@contracts/client";
import { apiEndpoints, requestEndpoint, resultNotice } from "../api/service";
import { allActions, isOperationAffordance } from "./affordances";
import { sameChild } from "./operationConflict";
import { hasDocumentSubject } from "../protocol/types";

import type { ApiResult, ServiceResponse } from "../protocol/types";
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

const reminderRevision = async (
  effect: Effect,
  reminderId: string,
  signal: AbortSignal,
): Promise<number | null | Failed> => {
  const result = await requestEndpoint(
    apiEndpoints.reminderGet,
    effect.identity.credential,
    { target: effect.target, reminderId },
    signal,
  );
  if (result.kind !== "outcome" || result.value.outcome.kind !== "SUCCEEDED") {
    if (isDisclosureRefused(result)) {
      return null;
    }
    return { kind: "failed", notice: resultNotice(result), refused: isDisclosureRefused(result) };
  }
  return (
    allActions(result.value.outcome.document).flatMap(({ task, binding, blocked }) =>
      task === effect.source.prepared.input.task &&
      blocked === null &&
      binding.kind === "REMINDER" &&
      "reminderId" in binding &&
      binding.reminderId === reminderId &&
      binding.target.kind === "ID" &&
      binding.target.caseId === effect.target.caseId
        ? [binding.expectedRevision]
        : [],
    )[0] ?? null
  );
};

const paymentRevision = async (
  effect: Effect,
  paymentId: string,
  signal: AbortSignal,
): Promise<number | null | Failed> => {
  let cursor: string | null = null;
  do {
    const result: ApiResult<ServiceResponse<typeof apiEndpoints.casePayments>> =
      // oxlint-disable-next-line eslint/no-await-in-loop -- The current page supplies the next cursor.
      await requestEndpoint(
        apiEndpoints.casePayments,
        effect.identity.credential,
        { target: effect.target, ...(cursor === null ? {} : { cursor }) },
        signal,
      );
    if (result.kind !== "outcome" || result.value.outcome.kind !== "SUCCEEDED") {
      return isDisclosureRefused(result)
        ? null
        : { kind: "failed", notice: resultNotice(result), refused: false };
    }
    const document: HumanDocument = result.value.outcome.document;
    if (!hasDocumentSubject(document, "CASE") || document.subject.caseId !== effect.target.caseId) {
      return incomplete;
    }
    const match = allActions(document).find(
      ({ task, blocked, binding }) =>
        task === effect.source.prepared.input.task &&
        blocked === null &&
        binding.kind === "CASE" &&
        "paymentId" in binding &&
        binding.paymentId === paymentId,
    );
    if (match !== undefined && "expectedRevision" in match.binding) {
      return match.binding.expectedRevision;
    }
    cursor = document.nextCursor;
  } while (cursor !== null && !signal.aborted);
  return null;
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
  const { task, binding } = effect.source.prepared.input;
  const matching = allActions(current).find(
    (action) =>
      action.task === task &&
      action.blocked === null &&
      isOperationAffordance(action) &&
      action.binding.kind === binding.kind &&
      sameChild(binding, action.binding),
  );
  let revision: number | null | Failed = null;
  if ("reminderId" in binding) {
    revision = await reminderRevision(effect, binding.reminderId, signal);
  } else if (matching !== undefined && "expectedRevision" in matching.binding) {
    revision = matching.binding.expectedRevision;
  } else if ("paymentId" in binding) {
    revision = await paymentRevision(effect, binding.paymentId, signal);
  }
  if (revision === null) {
    return { kind: "observed", value: { current, revision: null } };
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
