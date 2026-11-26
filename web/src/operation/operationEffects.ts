import {
  apiEndpoints,
  service,
  requestEndpoint,
  isCurrentCase,
  resultNotice,
  type ApiResult,
  type ServiceResponse,
} from "../api/service";
import { isDisclosureRefused } from "../api/outcomes";
import type { OperationAction, OperationEffect } from "./operationTypes";
import { mutationUncertain } from "../protocol/delivery";
import {
  journalBeforeSend,
  recoveryRecordFormat,
  sameRecoveryScope,
  type RecoveryScope,
  type RecoveryRecord,
  type RecoveryJournal,
} from "../protocol/recoveryJournal";
import { readConflict } from "./readConflict";

type Reply = ApiResult<
  | ServiceResponse<typeof apiEndpoints.operationExecute>
  | ServiceResponse<typeof apiEndpoints.operationPreview>
>;
const completion = (
  effect: Extract<
    OperationEffect,
    {
      kind: "PREVIEW" | "EXECUTE";
    }
  >,
  result: Reply,
): OperationAction => {
  if (result.kind === "outcome") {
    const { outcome } = result.value;
    if (outcome.kind === "ACCEPTED" || outcome.kind === "ALREADY_ACCEPTED") {
      return {
        type: "RECORD",
        receipt: outcome.document,
        identity: effect.identity,
      };
    }
    if (outcome.kind === "REVIEWED") {
      return {
        type: "REVIEW",
        review: outcome.review,
        identity: effect.identity,
      };
    }
  }
  return {
    type: "REFUSE",
    identity: effect.identity,
    notice: resultNotice(result),
    target: null,
    rejection:
      result.kind === "outcome" && result.value.outcome.kind === "REJECTED"
        ? result.value.outcome.rejection
        : undefined,
    uncertain: effect.kind === "EXECUTE" && mutationUncertain(result),
    disclosure: isDisclosureRefused(result),
  };
};
const runNetwork = async (
  effect: Extract<
    OperationEffect,
    {
      kind: "PREVIEW" | "EXECUTE" | "CHECK_OPERATION";
    }
  >,
  installationId?: string,
  signal?: AbortSignal,
): Promise<OperationAction> => {
  if (effect.kind === "CHECK_OPERATION") {
    const result = await service.operationGet(
      effect.source.prepared.input.operationId,
      effect.identity.credential,
      installationId,
      signal,
    );
    if (result.kind === "outcome") {
      if (result.value.outcome.kind === "ACCEPTED" && !result.value.outcome.byRequester) {
        return {
          type: "NOT_ACCEPTED",
          identity: effect.identity,
        };
      }
      if (result.value.outcome.kind === "ACCEPTED") {
        return {
          type: "RECORD",
          identity: effect.identity,
          receipt: result.value.outcome.document,
        };
      }
      if (result.value.outcome.kind === "PENDING") {
        return {
          type: "OPERATION_PENDING",
          identity: effect.identity,
          screen: result.value.outcome.document.screen,
        };
      }
      if (result.value.outcome.kind === "NOT_ACCEPTED") {
        return {
          type: "NOT_ACCEPTED",
          identity: effect.identity,
        };
      }
    }
    return {
      type: "REFUSE",
      identity: effect.identity,
      notice: resultNotice(result),
      target: null,
      uncertain: true,
      disclosure: isDisclosureRefused(result),
    };
  }
  return completion(
    effect,
    effect.kind === "PREVIEW"
      ? await service.preview(effect.source.prepared, effect.identity.credential, signal)
      : await service.execute(effect.source.prepared, effect.identity.credential, installationId),
  );
};
const ownedNetworkRead = async (
  effect: Extract<
    OperationEffect,
    {
      kind: "PREVIEW" | "CHECK_OPERATION";
    }
  >,
  reads: Map<string, AbortController>,
  installationId?: string,
): Promise<OperationAction> => {
  const controller = new AbortController();
  reads.set(effect.identity.id, controller);
  try {
    return await runNetwork(effect, installationId, controller.signal);
  } finally {
    reads.delete(effect.identity.id);
  }
};
const settleAttempt = async (
  journal: RecoveryJournal,
  attempts: Map<string, RecoveryRecord>,
  owned: RecoveryRecord,
): Promise<void> => {
  try {
    await journal.remove(owned);
  } catch {
    return;
  }
  if (attempts.get(owned.operationId) === owned) {
    attempts.delete(owned.operationId);
  }
};
const checkOwnedStatus = async (
  effect: Extract<
    OperationEffect,
    {
      kind: "CHECK_OPERATION";
    }
  >,
  scope: RecoveryScope,
  attempts: Map<string, RecoveryRecord>,
  journal: RecoveryJournal,
  current: () => boolean,
  reads: Map<string, AbortController>,
): Promise<OperationAction> => {
  const owned = attempts.get(effect.source.prepared.input.operationId);
  const stillCurrent = () => current() && owned !== undefined && sameRecoveryScope(scope, owned);
  if (owned === undefined || !stillCurrent()) {
    return {
      type: "REFUSE",
      identity: effect.identity,
      notice: {
        kind: "local",
        reason: "recoveryUnavailable",
      },
      target: null,
      uncertain: true,
      disclosure: false,
    };
  }
  // A passive read uses the existing attempt; it never publishes another journal write.
  const action = await ownedNetworkRead(effect, reads, owned.installationId);
  if (stillCurrent() && (action.type === "RECORD" || action.type === "NOT_ACCEPTED")) {
    await settleAttempt(journal, attempts, owned);
  }
  return action;
};
const readCaseDisclosure = async (
  effect: Extract<
    OperationEffect,
    {
      kind: "READ_CURRENT";
    }
  >,
  reads: Map<string, AbortController>,
): Promise<OperationAction> => {
  const controller = new AbortController();
  reads.set(effect.identity.id, controller);
  try {
    const result = await requestEndpoint(
      apiEndpoints.caseGet,
      effect.identity.credential,
      {
        target: {
          kind: "ID",
          caseId: effect.caseId,
        },
      },
      controller.signal,
    );
    if (result.kind === "outcome" && result.value.outcome.kind === "SUCCEEDED") {
      const value = result.value.outcome.document;
      if (isCurrentCase(value)) {
        return {
          type: "DISCLOSED",
          identity: effect.identity,
          value,
        };
      }
      return {
        type: "DISCLOSURE_READ_FAILED",
        identity: effect.identity,
        notice: {
          kind: "local",
          reason: "incomplete",
        },
        refused: false,
      };
    }
    return {
      type: "DISCLOSURE_READ_FAILED",
      identity: effect.identity,
      notice: resultNotice(result),
      refused: isDisclosureRefused(result),
    };
  } finally {
    reads.delete(effect.identity.id);
  }
};
type DisclosureEffect = Extract<
  OperationEffect,
  {
    kind: "READ_CURRENT" | "READ_CONFLICT";
  }
>;
const isDisclosureEffect = (effect: OperationEffect): effect is DisclosureEffect =>
  effect.kind === "READ_CURRENT" || effect.kind === "READ_CONFLICT";
const readDisclosure = (effect: DisclosureEffect, reads: Map<string, AbortController>) =>
  effect.kind === "READ_CONFLICT" ? readConflict(effect, reads) : readCaseDisclosure(effect, reads);
type ExecuteEffect = Extract<
  OperationEffect,
  {
    kind: "EXECUTE";
  }
>;
type AttemptContext = {
  journal: RecoveryJournal;
  scope: RecoveryScope;
  attempts: Map<string, RecoveryRecord>;
  current: () => boolean;
  handoff: () => void;
  upload: File | undefined;
  transfers: Map<string, AbortController>;
};
const executeOwnedAttempt = async (
  effect: ExecuteEffect,
  context: AttemptContext,
): Promise<OperationAction> => {
  const { journal, scope, current } = context;
  const { operationId } = effect.source.prepared.input;
  const executing = effect;
  const saved = await journal.list(scope).catch(() => null);
  if (saved === null) {
    return {
      type: "REFUSE",
      identity: effect.identity,
      notice: {
        kind: "local",
        reason: "recoveryUnavailable",
      },
      target: null,
      uncertain: false,
      disclosure: false,
    };
  }
  if (!current()) {
    return {
      type: "REFUSE",
      identity: effect.identity,
      notice: {
        kind: "local",
        reason: "cancelled",
      },
      target: null,
      uncertain: false,
      disclosure: false,
    };
  }
  let record = context.attempts.get(operationId);
  if (record === undefined) {
    record = {
      ...scope,
      format: recoveryRecordFormat,
      attemptId: effect.identity.id,
      operationId,
      subject: executing.source.prepared.input.binding.kind,
      act: executing.source.prepared.input.task,
    };
    context.attempts.set(operationId, record);
  }
  const owned = record;
  const stillCurrent = () => current() && sameRecoveryScope(scope, owned);
  const prepared = await journalBeforeSend(journal, owned, stillCurrent, () => {
    context.handoff();
    if (context.upload === undefined) {
      return runNetwork(executing, scope.installationId);
    }
    const controller = new AbortController();
    context.transfers.set(effect.identity.id, controller);
    return service
      .uploadAttachment(
        executing.source.prepared,
        context.upload,
        effect.identity.credential,
        scope.installationId,
        controller.signal,
      )
      .then((reply) => completion(executing, reply))
      .finally(() => {
        context.transfers.delete(effect.identity.id);
      });
  });
  if (prepared.kind === "notSent") {
    return {
      type: "REFUSE",
      identity: effect.identity,
      notice: {
        kind: "local",
        reason: prepared.reason === "storage" ? "recoveryUnavailable" : "cancelled",
      },
      target: null,
      uncertain: false,
      disclosure: false,
    };
  }
  const action = prepared.value;
  // An exact record already present can belong to a prior dispatch with no known outcome.
  if (
    action.type === "RECORD" ||
    action.type === "NOT_ACCEPTED" ||
    (prepared.publication === "inserted" && action.type === "REFUSE" && !action.uncertain)
  ) {
    await settleAttempt(journal, context.attempts, owned);
  }
  return action;
};
type NetworkEffect = Exclude<OperationEffect, { kind: "RECORDED" | "DISCLOSURE_REFUSED" }>;
type EffectContext = Omit<AttemptContext, "journal" | "scope"> & {
  journal: RecoveryJournal | undefined;
  scope: RecoveryScope | undefined;
  reads: Map<string, AbortController>;
};

export const runOperationEffect = (
  effect: NetworkEffect,
  context: EffectContext,
): Promise<OperationAction> => {
  const { journal, scope, attempts, current, reads } = context;
  if (isDisclosureEffect(effect)) {
    return readDisclosure(effect, reads);
  }
  if (effect.kind === "PREVIEW") {
    return ownedNetworkRead(effect, reads, scope?.installationId);
  }
  if (scope === undefined || journal === undefined) {
    return Promise.resolve({
      type: "REFUSE",
      identity: effect.identity,
      notice: { kind: "local", reason: "recoveryUnavailable" },
      target: null,
      uncertain: false,
      disclosure: false,
    });
  }
  return effect.kind === "CHECK_OPERATION"
    ? checkOwnedStatus(effect, scope, attempts, journal, current, reads)
    : executeOwnedAttempt(effect, { ...context, scope, journal });
};
