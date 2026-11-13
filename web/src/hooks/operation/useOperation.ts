import { createDraft } from "../../operation/operationDraft";
import { ownerScope, sameOwnerScope } from "../../protocol/ownerScope";
import type { MutationLockChange } from "../useWorkspaceMutationLock";
import { useEndpointCache } from "../EndpointProvider";
import { operationId as mintOperationId } from "../../operation/operationId";
import type { DateEntryPart } from "../../operation/dateEntryDraft";
import { readConflict } from "./readConflict";
import { rebasedTaskBinding } from "../../operation/operationConflict";
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
import { useContext, useEffect, useLayoutEffect, useReducer, useRef } from "react";
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
import { prepareRequest } from "../../protocol/preparedRequest";
import type { FormValues, FormValue } from "../../protocol/forms";
import { TaskInputError, type TaskBindingSource } from "../../operation/taskForm";
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
import type { LocalSource } from "../../operation/operationTypes";
import type { Notice } from "../../protocol/notices";
type Options = {
  subject: "CASE" | "PEOPLE" | "WORK" | "REMINDER" | "NOTE";
  prefill?: FormValues;
  binding?: TaskBindingSource;
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
  onLockChange: MutationLockChange;
  onRecorded?: (receipt: OperationReceipt) => void;
  onDisclosureRefused?: (notice: Notice) => void;
};
const targetKey = (options: Options): string =>
  JSON.stringify([
    options.subject,
    options.current?.context.caseId ?? null,
    options.targetIdentity ?? null,
    options.binding?.kind === "PEOPLE" ? (options.binding.principal ?? null) : null,
  ]);
type PeopleBinding = Extract<
  TaskBindingSource,
  {
    kind: "PEOPLE";
  }
>;
const initialBinding = (options: Options): TaskBindingSource => {
  if (options.binding !== undefined) {
    return options.binding;
  }
  if (options.subject === "PEOPLE") {
    return {
      kind: "PEOPLE",
    };
  }
  const { current } = options;
  if (options.subject === "CASE" && current === null) {
    return {
      kind: "NEW_CASE",
    };
  }
  if (current === null) {
    throw new Error("The task requires a current case binding.");
  }
  const target = {
    kind: "ID",
    caseId: current.context.caseId,
  } as const;
  switch (options.subject) {
    case "CASE":
      return {
        kind: "CASE",
        target,
        revision: current.context.revision,
      };
    case "WORK":
      return {
        kind: "WORK",
        target,
        workRevision: current.context.workRevision,
      };
    case "NOTE":
      return {
        kind: "NOTE",
        target,
      };
    case "REMINDER":
      return {
        kind: "REMINDER",
        target,
      };
  }
};
const fresh = (
  options: Options,
  kind = options.kind,
  prefill: FormValues = {},
  binding?: TaskBindingSource,
): OperationState => {
  const selectedBinding = binding ?? initialBinding(options);
  const values = {
    ...initialOperationValues(options.subject, kind, options.current),
    ...options.prefill,
    ...prefill,
  };
  const context = {
    generation: 0,
    owner: ownerScope(options.recoveryScope, options.contextEpoch ?? 0),
    epoch: options.contextEpoch ?? 0,
    credential: options.credential,
    target: targetKey(options),
  };
  return {
    outbox: [],
    draft: createDraft(
      { subject: options.subject, kind },
      targetKey(options),
      selectedBinding,
      values,
      context.owner,
    ),
    disclosed: {
      review: null,
      receipt: null,
      context,
      confirmedContext: context,
      caseObservation: options.current,
      read: null,
      conflict: null,
      refused: false,
      revision: 0,
      notice: null,
      fieldErrors: {},
      rejection: null,
    },
    delivery: {
      notice: null,
      owner: context.owner,
      credential: options.credential,
      phase: {
        kind: "EDITING",
        exposed: null,
      },
    },
  };
};
const identity = (options: Options, contextGeneration: number): EffectIdentity => {
  const owner = ownerScope(options.recoveryScope, options.contextEpoch ?? 0);
  if (owner === null) {
    throw new Error("A request requires a verified owner scope.");
  }
  return {
    owner,
    contextGeneration,
    id: crypto.randomUUID(),
    credential: options.credential,
    target: targetKey(options),
  };
};
const dirtyValues = (left: FormValues, right: FormValues): boolean =>
  Object.keys(left).length !== Object.keys(right).length ||
  Object.entries(left).some(([key, value]) => right[key] !== value);
type Reply = ApiResult<ServiceResponse<"operation.execute"> | ServiceResponse<"operation.preview">>;
const completionNotice = (result: Reply): Notice => resultNotice(result);
const completionUncertain = (
  effect: {
    kind: string;
  },
  result: Reply,
): boolean => effect.kind === "EXECUTE" && mutationUncertain(result);
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
    notice: completionNotice(result),
    target: null,
    rejection:
      result.kind === "outcome" && result.value.outcome.kind === "REJECTED"
        ? result.value.outcome.rejection
        : undefined,
    uncertain: completionUncertain(effect, result),
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
          finalAt: result.value.outcome.finalAt,
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
const checkOwnedStatus = async (
  effect: Extract<
    OperationEffect,
    {
      kind: "CHECK_OPERATION";
    }
  >,
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
    await journal.remove(owned).catch(() => undefined);
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
      "case.get",
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
  dispatch: React.Dispatch<OperationAction>;
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
      format: 2,
      attemptId: effect.identity.id,
      operationId,
      subject: executing.source.prepared.input.kind,
      act: executing.source.prepared.input.act.kind,
    };
    context.attempts.set(operationId, record);
  }
  const owned = record;
  const stillCurrent = () => current() && sameRecoveryScope(scope, owned);
  const prepared = await journalBeforeSend(journal, owned, stillCurrent, () => {
    context.dispatch({
      type: "HANDOFF",
      identity: effect.identity,
    });
    return runNetwork(executing, scope.installationId);
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
  if (action.type === "RECORD" || action.type === "NOT_ACCEPTED") {
    await journal.remove(owned).catch(() => undefined);
  }
  return action;
};
const effectMatchesOptions = (requestIdentity: EffectIdentity, options: Options) =>
  sameOwnerScope(
    requestIdentity.owner,
    ownerScope(options.recoveryScope, options.contextEpoch ?? 0),
  ) &&
  requestIdentity.credential === options.credential &&
  requestIdentity.target === targetKey(options);
const useEffects = (
  state: OperationState,
  dispatch: React.Dispatch<OperationAction>,
  options: Options,
  ownerVerified: boolean,
) => {
  useEffect(() => {
    if (
      !ownerVerified ||
      state.delivery.phase.kind !== "UNRESOLVED" ||
      state.delivery.phase.pending !== null ||
      options.recoveryScope === undefined
    ) {
      return;
    }
    const timer = window.setTimeout(() => {
      dispatch({
        type: "CHECK_OPERATION",
        identity: identity(options, state.disclosed.context.generation),
      });
    }, 1000);
    return () => {
      window.clearTimeout(timer);
    };
  }, [state.delivery.phase, state.disclosed.context.generation, dispatch, options, ownerVerified]);
  const started = useRef(new Set<string>());
  const alive = useRef(true);
  const latest = useRef({
    options,
    generation: state.disclosed.context.generation,
    disclosureRevision: state.disclosed.revision,
    ownerVerified,
  });
  const reads = useRef(new Map<string, AbortController>());
  useLayoutEffect(() => {
    if (
      !sameOwnerScope(
        ownerScope(latest.current.options.recoveryScope, latest.current.options.contextEpoch ?? 0),
        ownerScope(options.recoveryScope, options.contextEpoch ?? 0),
      ) ||
      latest.current.generation !== state.disclosed.context.generation ||
      latest.current.disclosureRevision !== state.disclosed.revision ||
      latest.current.options.credential !== options.credential ||
      targetKey(latest.current.options) !== targetKey(options)
    ) {
      for (const controller of reads.current.values()) {
        controller.abort();
      }
    }
    latest.current = {
      options,
      generation: state.disclosed.context.generation,
      disclosureRevision: state.disclosed.revision,
      ownerVerified,
    };
  }, [options, state.disclosed.context.generation, state.disclosed.revision, ownerVerified]);
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
    const queued = new Set(state.outbox.map((effect) => `${effect.kind}:${effect.identity.id}`));
    started.current = new Set([...started.current].filter((id) => queued.has(id)));
    for (const effect of state.outbox) {
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
        !ownerVerified ||
        !effectMatchesOptions(effect.identity, options) ||
        effect.identity.contextGeneration !== latest.current.generation ||
        state.disclosed.revision !== latest.current.disclosureRevision
      ) {
        continue;
      }
      if (effect.kind === "RECORDED") {
        if (state.disclosed.receipt !== null) {
          options.onRecorded?.(state.disclosed.receipt);
        }
      } else if (effect.kind === "DISCLOSURE_REFUSED") {
        options.onDisclosureRefused?.(effect.notice);
      } else {
        const current = () =>
          alive.current &&
          latest.current.ownerVerified &&
          effectMatchesOptions(effect.identity, latest.current.options) &&
          latest.current.disclosureRevision === state.disclosed.revision &&
          latest.current.generation === effect.identity.contextGeneration;
        const run = (): Promise<OperationAction> => {
          if (isDisclosureEffect(effect)) {
            return readDisclosure(effect, reads.current);
          }
          const journal = options.journal!;
          const scope = options.recoveryScope;
          if (effect.kind === "PREVIEW") {
            return ownedNetworkRead(effect, reads.current, options.recoveryScope?.installationId);
          }
          if (scope === undefined) {
            return Promise.resolve({
              type: "REFUSE",
              identity: effect.identity,
              notice: {
                kind: "local",
                reason: "recoveryUnavailable",
              },
              target: null,
              uncertain: false,
              disclosure: false,
            });
          }
          const { operationId } = effect.source.prepared.input;
          if (effect.kind === "CHECK_OPERATION") {
            return checkOwnedStatus(
              effect,
              scope,
              attempts.current.get(operationId),
              journal,
              current,
              reads.current,
            );
          }
          return executeOwnedAttempt(effect, {
            journal,
            scope,
            attempts: attempts.current,
            current,
            dispatch,
          });
        };
        void run().then((action) => {
          if (effect.kind === "EXECUTE" ? alive.current : current()) {
            dispatch(action);
          }
        });
      }
    }
  }, [state, dispatch, options, ownerVerified]);
};
const useLock = (
  state: OperationState,
  onLockChange: Options["onLockChange"],
  ownerChanged: boolean,
) => {
  const locked = !ownerChanged && operationView(state).busy;
  const lease = useRef<object>({});
  useLayoutEffect(() => {
    const heldLease = lease.current;
    onLockChange(locked, heldLease);
    return () => {
      onLockChange(false, heldLease);
    };
  }, [locked, onLockChange]);
};
type Dispatch = React.Dispatch<OperationAction>;
const prepareValues = (
  state: OperationState,
  options: Options,
  values: FormValues,
  binding: TaskBindingSource = state.draft.binding,
): LocalSource => {
  const task = operationEntry(state.draft.entry);
  const submission = assembleOperation(task, options.normalizeValues?.(values, task) ?? values, {
    ...binding,
    operationId: mintOperationId(),
  });
  return {
    prepared: prepareRequest("operation.execute", submission.command),
    submittedRows: submission.submittedRows,
  };
};
const reviewLatestDraft = (
  state: OperationState,
  dispatch: Dispatch,
  options: Options,
): Promise<void> => {
  const { conflict } = state.disclosed;
  if (
    conflict === null ||
    conflict.latest === null ||
    conflict.latest.revision === null ||
    state.disclosed.read !== null ||
    state.disclosed.refused
  ) {
    return Promise.resolve();
  }
  try {
    const { values } = state.draft;
    const source = prepareValues(
      state,
      options,
      values,
      rebasedTaskBinding(conflict.source.prepared.input, conflict.latest.revision),
    );
    dispatch({
      type: "REVIEW_LATEST",
      conflictId: conflict.identity.id,
      generation: state.draft.generation,
      identity: identity(options, state.disclosed.context.generation),
      values,
      source,
    });
  } catch (error) {
    dispatch({
      type: "REFUSE",
      notice: {
        kind: "local",
        reason: "invalidInput",
      },
      target: error instanceof TaskInputError ? error.input : null,
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
    const source = prepareValues(state, options, state.draft.values);
    dispatch({
      type: "PREVIEW",
      source,
      identity: identity(options, state.disclosed.context.generation),
      generation: state.draft.generation,
    });
  } catch (error) {
    dispatch({
      type: "REFUSE",
      notice: {
        kind: "local",
        reason: "invalidInput",
      },
      target: error instanceof TaskInputError ? error.input : null,
      uncertain: false,
      disclosure: false,
    });
  }
  return Promise.resolve();
};
const editing = (
  state: OperationState,
  dispatch: Dispatch,
  options: Options,
  ownerVerified: boolean,
) => {
  const view = operationView(state);
  const blocked =
    !ownerVerified ||
    view.busy ||
    view.priorUncertain ||
    view.phase === "ACCEPTED" ||
    state.draft.target !== targetKey(options);
  return {
    editFields: (values: FormValues, omitted: ReadonlyArray<string> = []) => {
      if (!blocked) {
        dispatch({
          type: "EDIT_FIELDS",
          values,
          omitted,
        });
      }
    },
    edit: (path: string, value: FormValue) => {
      if (!blocked) {
        dispatch({
          type: "EDIT",
          path,
          value: options.captureValue?.(path, value, operationEntry(state.draft.entry)) ?? value,
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
      const canRead =
        state.draft.owner !== null &&
        sameOwnerScope(
          state.draft.owner,
          ownerScope(options.recoveryScope, options.contextEpoch ?? 0),
        ) &&
        !view.busy &&
        !view.priorUncertain;
      if (!canRead || state.disclosed.read !== null) {
        return;
      }
      if (state.disclosed.conflict !== null) {
        dispatch({
          type: "READ_CONFLICT",
          identity: identity(options, state.disclosed.context.generation),
          conflictId: state.disclosed.conflict.identity.id,
        });
        return;
      }
      if (options.subject !== "PEOPLE" && options.current !== null) {
        dispatch({
          type: "READ_CURRENT",
          identity: identity(options, state.disclosed.context.generation),
          caseId: options.current.context.caseId,
          reference: "",
        });
      }
    },
    discard: (
      acknowledgedUncertainty = false,
      kind = options.kind,
      prefill?: FormValues,
      binding?: PeopleBinding,
    ) => {
      if (!view.busy && (!view.priorUncertain || acknowledgedUncertainty)) {
        dispatch({
          type: "RESET",
          state: fresh(
            state.disclosed.caseObservation !== null &&
              state.disclosed.confirmedContext === state.disclosed.context &&
              state.draft.target === targetKey(options)
              ? {
                  ...options,
                  current: state.disclosed.caseObservation,
                }
              : options,
            kind,
            prefill,
            binding ??
              (state.draft.target === targetKey(options) ? state.draft.binding : undefined),
          ),
          abandon: acknowledgedUncertainty,
        });
      }
    },
    preview: () => (blocked ? Promise.resolve() : previewDraft(state, dispatch, options)),
  };
};
const actions = (
  state: OperationState,
  dispatch: Dispatch,
  options: Options,
  ownerVerified: boolean,
) => ({
  ...editing(state, dispatch, options, ownerVerified),
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
  checkOperation: () => {
    if (!ownerVerified) {
      return;
    }
    dispatch({
      type: "CHECK_OPERATION",
      identity: identity(options, state.disclosed.context.generation),
    });
  },
  execute: () => {
    if (!ownerVerified) {
      return Promise.resolve();
    }
    dispatch({
      type: "EXECUTE",
      identity: identity(options, state.disclosed.context.generation),
    });
    return Promise.resolve();
  },
});
const hasCurrentDisclosure = (state: OperationState, options: Options, epoch: number): boolean =>
  sameOwnerScope(state.disclosed.context.owner, ownerScope(options.recoveryScope, epoch)) &&
  state.disclosed.confirmedContext === state.disclosed.context &&
  state.disclosed.context.credential === options.credential &&
  state.disclosed.context.epoch === epoch &&
  state.draft.target === targetKey(options) &&
  !state.disclosed.refused;
/** The first verified requester owns this coordinator; refresh never adopts a different owner. */
const requesterOffersCreation = (requester: ReturnType<typeof useRequesterContext>) =>
  requester?.context.tasks.includes("case.open") === true;
const useCreationDraftRestore = (
  state: OperationState,
  dispatch: Dispatch,
  options: Options,
  ownerVerified: boolean,
  requester: ReturnType<typeof useRequesterContext>,
) => {
  // Only a new successful context observation supplies proof; stale offers never renew it.
  const observed = useRef({
    requester,
    revision: state.disclosed.revision,
    pending: false,
  });
  useLayoutEffect(() => {
    if (observed.current.requester !== requester) {
      observed.current = {
        requester,
        revision: state.disclosed.revision,
        pending: ownerVerified && requesterOffersCreation(requester),
      };
    }
    const proof = observed.current;
    if (!proof.pending) {
      return;
    }
    if (proof.revision !== state.disclosed.revision || !ownerVerified) {
      proof.pending = false;
      return;
    }
    if (
      !sameOwnerScope(
        state.disclosed.context.owner,
        ownerScope(options.recoveryScope, options.contextEpoch ?? 0),
      ) ||
      state.disclosed.context.target !== targetKey(options) ||
      state.disclosed.context.epoch !== options.contextEpoch ||
      state.disclosed.context.credential !== options.credential
    ) {
      return;
    }
    // Context alignment may take another render, but refusal/reset ordering cannot be bypassed.
    proof.pending = false;
    if (canRestoreCreationDraft(state)) {
      dispatch({
        type: "RESTORE_CREATION_DRAFT",
        identity: identity(options, state.disclosed.context.generation),
        editing: state.delivery.phase,
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
        installationId: requester.context.installationId,
        actorId: requester.context.actorId,
      };
const operationOwnership = (state: OperationState, options: Options, contextEpoch: number) => {
  const observedOwner = ownerScope(options.recoveryScope, contextEpoch);
  const original = state.delivery.owner;
  const sessionMatches = original !== null && original.sessionEpoch === contextEpoch;
  const scopeVerified =
    sessionMatches && observedOwner !== null && sameOwnerScope(original, observedOwner);
  const ownerVerified = scopeVerified && state.delivery.credential === options.credential;
  const ownerChanged =
    original !== null &&
    (state.delivery.credential !== options.credential ||
      !sessionMatches ||
      (observedOwner !== null && !sameOwnerScope(original, observedOwner)));
  return { ownerVerified, scopeVerified, ownerChanged };
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
    ...(recoveryScope === undefined
      ? {}
      : {
          recoveryScope,
        }),
  };
  const [state, dispatch] = useReducer(operationReducer, options, fresh);
  const { ownerVerified, scopeVerified, ownerChanged } = operationOwnership(
    state,
    options,
    contextEpoch,
  );
  const cache = useEndpointCache();
  useLayoutEffect(
    () =>
      cache?.subscribeInvalidation(() => {
        dispatch({
          type: "INVALIDATE",
          refused: true,
        });
      }),
    [cache],
  );
  const target = targetKey(options);
  useLayoutEffect(() => {
    dispatch({
      type: "CONTEXT",
      owner: ownerScope(options.recoveryScope, contextEpoch),
      epoch: contextEpoch,
      credential: options.credential,
      target,
    });
  }, [options.credential, target, contextEpoch, options.recoveryScope]);
  useLayoutEffect(() => {
    if (ownerChanged) {
      dispatch({
        type: "CLEAR_DATE_ENTRIES",
      });
    }
  }, [ownerChanged]);
  useCreationDraftRestore(state, dispatch, options, ownerVerified, requester);
  useEffects(state, dispatch, options, scopeVerified);
  useLock(state, options.onLockChange, ownerChanged);
  const view = operationView(state);
  const disclosureCurrent = hasCurrentDisclosure(state, options, contextEpoch);
  const controls = actions(state, dispatch, options, ownerVerified);
  return {
    ...view,
    task: operationEntry(state.draft.entry),
    bindingSource: state.draft.binding,
    ownerVerified,
    ...controls,
    preview: () => (ownerVerified ? controls.preview() : Promise.resolve()),
    execute: () => (ownerVerified ? controls.execute() : Promise.resolve()),
    confirm: (value: boolean) => {
      if (ownerVerified && disclosureCurrent) {
        dispatch({
          type: "CONFIRM",
          value,
        });
      }
    },
    captureDateEntry: (
      path: string,
      canonical: string,
      parts: ReadonlyArray<DateEntryPart> | null,
    ) => {
      if (ownerVerified && disclosureCurrent) {
        dispatch({
          type: "DATE_ENTRY",
          path,
          canonical,
          parts,
        });
      }
    },
    dirty:
      dirtyValues(state.draft.values, state.draft.baseline) ||
      Object.keys(state.draft.dateEntries).length > 0,
    disclosureCurrent,
    currentCase: disclosureCurrent ? state.disclosed.caseObservation : null,
    revalidating: state.disclosed.read !== null,
    canRevalidateCase: options.subject !== "PEOPLE" && options.current !== null,
    review: disclosureCurrent ? view.review : null,
    receipt: disclosureCurrent ? view.receipt : null,
    contextChanged: state.draft.target !== target,
  };
};
export type OperationModel = ReturnType<typeof useOperation>;
