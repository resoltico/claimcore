import type { DateEntryPart } from "../../operation/dateEntryDraft";
import { readConflict } from "./readConflict";
import { latestOperationValues } from "../../operation/operationConflict";
import { receiptCorrelates, replyCorrelates } from "../../protocol/replyCorrelation";
import { SessionContext } from "../../session/SessionContext";
import { useRequesterContext } from "../useRequesterContext";
import { useRecoveryJournal } from "../../recovery/JournalContext";
import {
  journalBeforeSend,
  sameRecoveryScope,
  type RecoveryScope,
  type RecoveryRecord,
  type RecoveryJournal,
} from "../../protocol/recoveryJournal";
import { useContext, useEffect, useLayoutEffect, useReducer, useRef, useState } from "react";
import {
  service,
  requestEndpoint,
  isCurrentCase,
  resultNotice,
  type CurrentCase,
  type ApiResult,
  type ServiceResponse,
  type OperationReceipt,
} from "../../api/service";
import { isDisclosureRefused } from "../../api/outcomes";
import { mutationUncertain } from "../../protocol/delivery";
import { prepareRequest, ProtocolInputError } from "../../protocol/preparedRequest";
import { buildRequest } from "../../protocol/client";
import type { FormValues, FormValue } from "../../protocol/forms";
import { initialOperationValues, assembleOperation } from "../../operation/operationForm";
import { operationEntry } from "../../protocol/operationCatalog";
import {
  operationReducer,
  operationView,
  canRestoreCreationDraft,
  type OperationState,
  type OperationEffect,
  type OperationAction,
  type EffectIdentity,
} from "../../operation/operationState";
import type { Notice } from "../../protocol/notices";
type Options = {
  subject: "CASE" | "AUTHORITY" | "WORK" | "REMINDER";
  prefill?: FormValues;
  targetIdentity?: string;
  kind: string;
  current: CurrentCase | null;
  credential: string;
  contextEpoch?: number;
  recoveryScope?: RecoveryScope;
  journal?: RecoveryJournal;
  normalizeValues?: (values: FormValues, entry: ReturnType<typeof operationEntry>) => FormValues;
  captureValue?: (
    path: string,
    value: FormValue,
    entry: ReturnType<typeof operationEntry>,
  ) => FormValue;
  onLockChange: (locked: boolean) => void;
  onRecorded?: (receipt: OperationReceipt) => void;
  onDisclosureRefused?: (notice: Notice) => void;
};
const targetKey = (options: Options): string =>
  `${options.subject}:${options.recoveryScope?.installationId ?? "UNBOUND"}:${options.recoveryScope?.actorId ?? "UNBOUND"}:${options.current?.caseId ?? "NEW"}:${options.current?.caseReference.toUpperCase() ?? "NEW"}:${options.targetIdentity ?? ""}`;
const fresh = (options: Options, kind = options.kind, prefill: FormValues = {}): OperationState => {
  const values = {
    ...initialOperationValues(options.subject, kind, options.current),
    ...options.prefill,
    ...prefill,
  };
  const context = {
    generation: 0,
    epoch: options.contextEpoch ?? 0,
    credential: options.credential,
    target: targetKey(options),
  };
  return {
    target: targetKey(options),
    entry: { subject: options.subject, kind },
    context,
    disclosureContext: context,
    caseObservation: options.current,
    disclosureRead: null,
    conflict: null,
    disclosureRefused: false,
    disclosureRevision: 0,
    values,
    dateEntries: {},
    baseline: values,
    generation: 0,
    operation: {
      kind: "EDITING",
      exposed: null,
    },
    effects: [],
    notice: null,
    fieldErrors: {},
    pendingDiscard: null,
  };
};
const identity = (options: Options, contextGeneration: number): EffectIdentity => ({
  contextGeneration,
  id: crypto.randomUUID(),
  credential: options.credential,
  target: targetKey(options),
});
const dirtyValues = (left: FormValues, right: FormValues): boolean =>
  Object.keys(left).length !== Object.keys(right).length ||
  Object.entries(left).some(([key, value]) => right[key] !== value);
type Reply = ApiResult<ServiceResponse<"operation.execute"> | ServiceResponse<"operation.preview">>;
const incompleteSuccess = (result: Reply): boolean =>
  result.kind === "outcome" &&
  ["ACCEPTED", "ALREADY_ACCEPTED", "REVIEWED"].includes(result.value.outcome.kind);
const completionNotice = (result: Reply): Notice =>
  incompleteSuccess(result) ? { kind: "local", reason: "incomplete" } : resultNotice(result);
const completionUncertain = (effect: { kind: string }, result: Reply): boolean =>
  effect.kind === "EXECUTE" && (mutationUncertain(result) || incompleteSuccess(result));
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
    if (
      (outcome.kind === "ACCEPTED" || outcome.kind === "ALREADY_ACCEPTED") &&
      receiptCorrelates(effect.source.prepared.input, outcome.data)
    ) {
      return {
        type: "RECORD",
        receipt: outcome.data,
        identity: effect.identity,
      };
    }
    if (
      outcome.kind === "REVIEWED" &&
      replyCorrelates("operation.preview", effect.source.prepared.input, result.value)
    ) {
      return {
        type: "REVIEW",
        review: outcome.data.review,
        identity: effect.identity,
      };
    }
  }
  return {
    type: "REFUSE",
    identity: effect.identity,
    notice: completionNotice(result),
    target:
      result.kind === "outcome" &&
      result.value.outcome.kind === "REJECTED" &&
      "target" in result.value.outcome.data
        ? result.value.outcome.data.target
        : null,
    uncertain: completionUncertain(effect, result),
    disclosure: isDisclosureRefused(result),
  };
};
const runNetwork = async (
  effect: Extract<
    OperationEffect,
    {
      kind: "PREVIEW" | "EXECUTE" | "CHECK_STATUS";
    }
  >,
  installationId?: string,
  signal?: AbortSignal,
): Promise<OperationAction> => {
  if (effect.kind === "CHECK_STATUS") {
    const result = await service.status(
      effect.source.prepared.input.operationId,
      effect.identity.credential,
      installationId,
      signal,
    );
    if (result.kind === "outcome") {
      if (
        result.value.outcome.kind === "ACCEPTED" &&
        receiptCorrelates(effect.source.prepared.input, result.value.outcome.data)
      ) {
        return {
          type: "RECORD",
          identity: effect.identity,
          receipt: result.value.outcome.data,
        };
      }
      if (result.value.outcome.kind === "UNRESOLVED") {
        return { type: "STATUS_UNRESOLVED", identity: effect.identity };
      }
      if (result.value.outcome.kind === "NOT_ACCEPTED") {
        return {
          type: "NOT_ACCEPTED",
          identity: effect.identity,
          id: crypto.randomUUID(),
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
  effect: Extract<OperationEffect, { kind: "PREVIEW" | "CHECK_STATUS" }>,
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
const checkOwnedStatus = async (
  effect: Extract<OperationEffect, { kind: "CHECK_STATUS" }>,
  scope: RecoveryScope,
  owned: RecoveryRecord | undefined,
  journal: RecoveryJournal,
  current: () => boolean,
  reads: Map<string, AbortController>,
): Promise<OperationAction> => {
  const stillCurrent = () => current() && owned !== undefined && sameRecoveryScope(scope, owned);
  if (owned === undefined || !stillCurrent()) {
    return {
      type: "REFUSE",
      identity: effect.identity,
      notice: { kind: "local", reason: "recoveryUnavailable" },
      target: null,
      uncertain: true,
      disclosure: false,
    };
  }
  // A passive read uses the existing attempt; it never publishes another journal write.
  const action = await ownedNetworkRead(effect, reads, owned.installationId);
  if (stillCurrent() && (action.type === "RECORD" || action.type === "NOT_ACCEPTED")) {
    await journal.remove(owned).catch(() => undefined);
  }
  return action;
};
const readCaseDisclosure = async (
  effect: Extract<OperationEffect, { kind: "READ_CURRENT" }>,
  reads: Map<string, AbortController>,
): Promise<OperationAction> => {
  const controller = new AbortController();
  reads.set(effect.identity.id, controller);
  try {
    const result = await requestEndpoint(
      "case.get",
      effect.identity.credential,
      { target: { kind: "ID", caseId: effect.caseId } },
      controller.signal,
    );
    if (result.kind === "outcome" && result.value.outcome.kind === "SUCCEEDED") {
      const value = result.value.outcome.data;
      if (
        isCurrentCase(value) &&
        value.caseId === effect.caseId &&
        value.caseReference.toUpperCase() === effect.reference.toUpperCase()
      ) {
        return { type: "DISCLOSED", identity: effect.identity, value };
      }
      return {
        type: "DISCLOSURE_READ_FAILED",
        identity: effect.identity,
        notice: { kind: "local", reason: "incomplete" },
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
type DisclosureEffect = Extract<OperationEffect, { kind: "READ_CURRENT" | "READ_CONFLICT" }>;
const isDisclosureEffect = (effect: OperationEffect): effect is DisclosureEffect =>
  effect.kind === "READ_CURRENT" || effect.kind === "READ_CONFLICT";
const readDisclosure = (effect: DisclosureEffect, reads: Map<string, AbortController>) =>
  effect.kind === "READ_CONFLICT" ? readConflict(effect, reads) : readCaseDisclosure(effect, reads);
const useEffects = (
  state: OperationState,
  dispatch: React.Dispatch<OperationAction>,
  options: Options,
) => {
  const started = useRef(new Set<string>());
  const alive = useRef(true);
  const latest = useRef({ options, generation: state.context.generation });
  const reads = useRef(new Map<string, AbortController>());
  useLayoutEffect(() => {
    if (
      latest.current.generation !== state.context.generation ||
      latest.current.options.credential !== options.credential ||
      targetKey(latest.current.options) !== targetKey(options)
    ) {
      for (const controller of reads.current.values()) {
        controller.abort();
      }
    }
    latest.current = { options, generation: state.context.generation };
  }, [options, state.context.generation]);
  const attempts = useRef(new Map<string, RecoveryRecord>());
  useEffect(() => {
    alive.current = true;
    const ownedReads = reads.current;
    return () => {
      alive.current = false;
      for (const controller of ownedReads.values()) {
        controller.abort();
      }
    };
  }, []);
  useEffect(() => {
    const queued = new Set(state.effects.map((effect) => `${effect.kind}:${effect.identity.id}`));
    started.current = new Set([...started.current].filter((id) => queued.has(id)));
    for (const effect of state.effects) {
      const scheduled = `${effect.kind}:${effect.identity.id}`;
      if (started.current.has(scheduled)) {
        continue;
      }
      started.current.add(scheduled);
      dispatch({
        type: "EFFECT_STARTED",
        id: effect.identity.id,
      });
      if (
        effect.identity.contextGeneration !== latest.current.generation ||
        effect.identity.credential !== options.credential ||
        effect.identity.target !== targetKey(options)
      ) {
        continue;
      }
      if (effect.kind === "RECORDED") {
        options.onRecorded?.(effect.receipt);
      } else if (effect.kind === "DISCLOSURE_REFUSED") {
        options.onDisclosureRefused?.(effect.notice);
      } else {
        const current = () =>
          alive.current &&
          latest.current.generation === effect.identity.contextGeneration &&
          effect.identity.credential === latest.current.options.credential &&
          effect.identity.target === targetKey(latest.current.options);
        const run = async (): Promise<OperationAction> => {
          if (isDisclosureEffect(effect)) {
            return readDisclosure(effect, reads.current);
          }
          const journal = options.journal!;
          const scope = options.recoveryScope;
          if (effect.kind === "PREVIEW") {
            return ownedNetworkRead(effect, reads.current, options.recoveryScope?.installationId);
          }
          if (scope === undefined) {
            return {
              type: "REFUSE",
              identity: effect.identity,
              notice: { kind: "local", reason: "recoveryUnavailable" },
              target: null,
              uncertain: false,
              disclosure: false,
            };
          }
          const { operationId } = effect.source.prepared.input;
          if (effect.kind === "CHECK_STATUS") {
            return checkOwnedStatus(
              effect,
              scope,
              attempts.current.get(operationId),
              journal,
              current,
              reads.current,
            );
          }
          let record = attempts.current.get(operationId);
          if (record === undefined) {
            record = {
              ...scope,
              format: 2,
              attemptId: effect.identity.id,
              operationId,
              subject: effect.source.prepared.input.kind,
              act: effect.source.prepared.input.act.kind,
            };
            attempts.current.set(operationId, record);
          }
          const owned = record;
          const stillCurrent = () => current() && sameRecoveryScope(scope, owned);
          const prepared = await journalBeforeSend(journal, owned, stillCurrent, () => {
            dispatch({ type: "HANDOFF", identity: effect.identity });
            return runNetwork(effect, options.recoveryScope?.installationId);
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
          if (stillCurrent() && (action.type === "RECORD" || action.type === "NOT_ACCEPTED")) {
            await journal.remove(owned).catch(() => undefined);
          }
          return action;
        };
        void run().then((action) => {
          if (current()) {
            dispatch(action);
          }
        });
      }
    }
  }, [state, dispatch, options]);
};
const useLock = (state: OperationState, onLockChange: Options["onLockChange"]) => {
  const { busy: locked } = operationView(state);
  useLayoutEffect(() => {
    onLockChange(locked);
    return () => {
      onLockChange(false);
    };
  }, [locked, onLockChange]);
};
type Dispatch = React.Dispatch<OperationAction>;
const prepareValues = (state: OperationState, options: Options, values: FormValues) =>
  prepareRequest(
    "operation.execute",
    buildRequest(
      "operation.execute",
      assembleOperation(
        operationEntry(state.entry),
        options.normalizeValues?.(values, operationEntry(state.entry)) ?? values,
      ),
    ),
  );
const reviewLatestDraft = (
  state: OperationState,
  dispatch: Dispatch,
  options: Options,
): Promise<void> => {
  const { conflict } = state;
  if (
    conflict === null ||
    conflict.latest === null ||
    conflict.latest.revision === null ||
    state.disclosureRead !== null ||
    state.disclosureRefused
  ) {
    return Promise.resolve();
  }
  try {
    const values = latestOperationValues(
      operationEntry(state.entry),
      state.values,
      conflict.latest.revision,
      crypto.randomUUID(),
    );
    const prepared = prepareValues(state, options, values);
    dispatch({
      type: "REVIEW_LATEST",
      conflictId: conflict.identity.id,
      generation: state.generation,
      identity: identity(options, state.context.generation),
      values,
      prepared,
    });
  } catch (error) {
    dispatch({
      type: "REFUSE",
      notice: { kind: "local", reason: "invalidInput" },
      target: error instanceof ProtocolInputError ? error.path.join(".") : null,
      uncertain: false,
      disclosure: false,
    });
  }
  return Promise.resolve();
};
const previewDraft = (
  state: OperationState,
  dispatch: Dispatch,
  options: Options,
): Promise<void> => {
  try {
    const view = operationView(state);
    const prepared = view.prepared ?? prepareValues(state, options, state.values);
    dispatch({
      type: "PREVIEW",
      prepared,
      identity: identity(options, state.context.generation),
      generation: state.generation,
    });
  } catch (error) {
    dispatch({
      type: "REFUSE",
      notice: {
        kind: "local",
        reason: "invalidInput",
      },
      target: error instanceof ProtocolInputError ? error.path.join(".") : null,
      uncertain: false,
      disclosure: false,
    });
  }
  return Promise.resolve();
};
const editing = (state: OperationState, dispatch: Dispatch, options: Options) => {
  const view = operationView(state);
  const blocked =
    view.busy ||
    view.priorUncertain ||
    view.phase === "ACCEPTED" ||
    state.target !== targetKey(options);
  return {
    editFields: (values: FormValues, omitted: ReadonlyArray<string> = []) => {
      if (!blocked) {
        dispatch({ type: "EDIT_FIELDS", values, omitted, id: crypto.randomUUID() });
      }
    },
    edit: (path: string, value: FormValue) => {
      if (!blocked) {
        dispatch({
          type: "EDIT",
          path,
          value: options.captureValue?.(path, value, operationEntry(state.entry)) ?? value,
          id: crypto.randomUUID(),
        });
      }
    },
    returnToEdit: () => {
      if (!blocked) {
        dispatch({
          type: "RETURN",
        });
      }
    },
    confirm: (value: boolean) => {
      if (!blocked) {
        dispatch({
          type: "CONFIRM",
          value,
        });
      }
    },
    reviewLatest: () => (blocked ? Promise.resolve() : reviewLatestDraft(state, dispatch, options)),
    revalidateDisclosure: () => {
      if (!blocked && state.conflict !== null && state.disclosureRead === null) {
        dispatch({
          type: "READ_CONFLICT",
          identity: identity(options, state.context.generation),
          conflictId: state.conflict.identity.id,
        });
        return;
      }
      if (
        !blocked &&
        state.disclosureRead === null &&
        options.subject !== "AUTHORITY" &&
        options.current !== null
      ) {
        dispatch({
          type: "READ_CURRENT",
          identity: identity(options, state.context.generation),
          caseId: options.current.caseId,
          reference: options.current.caseReference,
        });
      }
    },
    discard: (acknowledgedUncertainty = false, kind = options.kind, prefill?: FormValues) => {
      if (!view.busy && (!view.priorUncertain || acknowledgedUncertainty)) {
        dispatch({
          type: "RESET",
          state: fresh(
            state.caseObservation !== null &&
              state.disclosureContext === state.context &&
              state.target === targetKey(options)
              ? { ...options, current: state.caseObservation }
              : options,
            kind,
            prefill,
          ),
          abandon: acknowledgedUncertainty,
        });
      }
    },
    preview: () => (blocked ? Promise.resolve() : previewDraft(state, dispatch, options)),
  };
};
const actions = (state: OperationState, dispatch: Dispatch, options: Options) => ({
  ...editing(state, dispatch, options),
  requestDiscard: (kind: string | null) => {
    dispatch({
      type: "DISCARD",
      kind,
    });
  },
  restoreDisclosure: () => {
    dispatch({
      type: "RESTORE",
    });
  },
  invalidateDisclosure: () => {
    dispatch({
      type: "INVALIDATE",
      refused: true,
    });
  },
  checkStatus: () => {
    dispatch({
      type: "CHECK_STATUS",
      identity: identity(options, state.context.generation),
    });
  },
  execute: () => {
    dispatch({
      type: "EXECUTE",
      identity: identity(options, state.context.generation),
    });
    return Promise.resolve();
  },
});
const hasCurrentDisclosure = (state: OperationState, options: Options, epoch: number): boolean =>
  state.disclosureContext === state.context &&
  state.context.credential === options.credential &&
  state.context.epoch === epoch &&
  state.target === targetKey(options) &&
  !state.disclosureRefused;
/** The first verified requester owns this coordinator; refresh never adopts a different owner. */
const useVerifiedOperationOwner = (scope: RecoveryScope | undefined) => {
  const [owner, setOwner] = useState<RecoveryScope | null>(null);
  if (owner === null && scope !== undefined) {
    setOwner(scope);
  }
  return {
    verified: owner !== null && scope !== undefined && sameRecoveryScope(owner, scope),
    changed: owner !== null && scope !== undefined && !sameRecoveryScope(owner, scope),
  };
};
const requesterOffersCreation = (requester: ReturnType<typeof useRequesterContext>) =>
  requester?.offers.some((offer) => offer.kind === "OPEN") === true;
const useCreationDraftRestore = (
  state: OperationState,
  dispatch: Dispatch,
  options: Options,
  ownerVerified: boolean,
  requester: ReturnType<typeof useRequesterContext>,
) => {
  // Only a new successful context observation supplies proof; stale offers never renew it.
  const observed = useRef({ requester, revision: state.disclosureRevision, pending: false });
  useLayoutEffect(() => {
    if (observed.current.requester !== requester) {
      observed.current = {
        requester,
        revision: state.disclosureRevision,
        pending: ownerVerified && requesterOffersCreation(requester),
      };
    }
    const proof = observed.current;
    if (!proof.pending) {
      return;
    }
    if (proof.revision !== state.disclosureRevision || !ownerVerified) {
      proof.pending = false;
      return;
    }
    if (
      state.context.target !== targetKey(options) ||
      state.context.epoch !== options.contextEpoch ||
      state.context.credential !== options.credential
    ) {
      return;
    }
    // Context alignment may take another render, but refusal/reset ordering cannot be bypassed.
    proof.pending = false;
    if (canRestoreCreationDraft(state)) {
      dispatch({
        type: "RESTORE_CREATION_DRAFT",
        identity: identity(options, state.context.generation),
        editing: state.operation,
        disclosureRevision: proof.revision,
      });
    }
  }, [dispatch, options, ownerVerified, requester, state]);
};
const requesterRecoveryScope = (
  requester: ReturnType<typeof useRequesterContext>,
): RecoveryScope | undefined =>
  requester === null
    ? undefined
    : {
        serviceOrigin: location.origin,
        installationId: requester.installationId,
        actorId: requester.actorId,
      };
/** One pure state owner; refs contain only effect scheduling and mounted-lifetime controls. */
export const useOperation = (supplied: Options) => {
  const requester = useRequesterContext();
  const journal = useRecoveryJournal();
  const session = useContext(SessionContext);
  const contextEpoch = supplied.contextEpoch ?? session?.state.epoch ?? 0;
  const recoveryScope = supplied.recoveryScope ?? requesterRecoveryScope(requester);
  const options: Options = {
    ...supplied,
    contextEpoch,
    journal: supplied.journal ?? journal,
    ...(recoveryScope === undefined ? {} : { recoveryScope }),
  };
  const { verified: ownerVerified, changed: ownerChanged } = useVerifiedOperationOwner(
    options.recoveryScope,
  );
  const [state, dispatch] = useReducer(operationReducer, options, fresh);
  const target = targetKey(options);
  useLayoutEffect(() => {
    dispatch({
      type: "CONTEXT",
      epoch: contextEpoch,
      credential: options.credential,
      target,
    });
  }, [options.credential, target, contextEpoch]);
  useLayoutEffect(() => {
    if (ownerChanged) {
      dispatch({ type: "CLEAR_DATE_ENTRIES" });
    }
  }, [ownerChanged]);
  useCreationDraftRestore(state, dispatch, options, ownerVerified, requester);
  useEffects(state, dispatch, options);
  useLock(state, options.onLockChange);
  const view = operationView(state);
  const disclosureCurrent = hasCurrentDisclosure(state, options, contextEpoch);
  return {
    ...view,
    ownerVerified,
    ...actions(state, dispatch, options),
    captureDateEntry: (
      path: string,
      canonical: string,
      parts: ReadonlyArray<DateEntryPart> | null,
    ) => {
      if (ownerVerified && disclosureCurrent) {
        dispatch({ type: "DATE_ENTRY", path, canonical, parts, id: crypto.randomUUID() });
      }
    },
    dirty: dirtyValues(state.values, state.baseline) || Object.keys(state.dateEntries).length > 0,
    disclosureCurrent,
    currentCase: disclosureCurrent ? state.caseObservation : null,
    revalidating: state.disclosureRead !== null,
    canRevalidateCase: options.subject !== "AUTHORITY" && options.current !== null,
    review: disclosureCurrent ? view.review : null,
    receipt: disclosureCurrent ? view.receipt : null,
    contextChanged: state.target !== target,
  };
};
export type OperationModel = ReturnType<typeof useOperation>;
