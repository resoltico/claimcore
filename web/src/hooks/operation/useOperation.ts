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
import { prepareRequest, ProtocolInputError } from "../../protocol/preparedRequest";
import { buildRequest } from "../../protocol/client";
import type { FormValues, FormValue } from "../../protocol/forms";
import {
  initialOperationValues,
  assembleOperation,
  operationEntry,
} from "../../operation/operationForm";
import {
  operationReducer,
  operationView,
  type OperationState,
  type OperationEffect,
  type OperationAction,
  type EffectIdentity,
} from "../../operation/operationState";
import type { Notice } from "../../protocol/notices";
type Options = {
  subject: "CASE" | "AUTHORITY";
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
  `${options.subject}:${options.recoveryScope?.installationId ?? "UNBOUND"}:${options.recoveryScope?.actorId ?? "UNBOUND"}:${options.current?.caseId ?? "NEW"}:${options.current?.caseReference.toUpperCase() ?? "NEW"}`;
const fresh = (options: Options, kind = options.kind, prefill: FormValues = {}): OperationState => {
  const values = { ...initialOperationValues(options.subject, kind, options.current), ...prefill };
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
    disclosureRefused: false,
    values,
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
        receipt: outcome.data,
        identity: effect.identity,
      };
    }
    if (outcome.kind === "REVIEWED") {
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
    notice: resultNotice(result),
    target:
      result.kind === "outcome" &&
      result.value.outcome.kind === "REJECTED" &&
      "target" in result.value.outcome.data
        ? result.value.outcome.data.target
        : null,
    uncertain: effect.kind === "EXECUTE" && mutationUncertain(result),
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
): Promise<OperationAction> => {
  if (effect.kind === "CHECK_STATUS") {
    const result = await service.status(
      effect.source.prepared.input.operationId,
      effect.identity.credential,
      installationId,
    );
    if (result.kind === "outcome") {
      if (result.value.outcome.kind === "ACCEPTED") {
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
      ? await service.preview(effect.source.prepared, effect.identity.credential)
      : await service.execute(effect.source.prepared, effect.identity.credential, installationId),
  );
};
const checkOwnedStatus = async (
  effect: Extract<OperationEffect, { kind: "CHECK_STATUS" }>,
  scope: RecoveryScope,
  owned: RecoveryRecord | undefined,
  journal: RecoveryJournal,
  current: () => boolean,
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
  const action = await runNetwork(effect, owned.installationId);
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
          if (effect.kind === "READ_CURRENT") {
            return readCaseDisclosure(effect, reads.current);
          }
          const journal = options.journal!;
          const scope = options.recoveryScope;
          if (effect.kind === "PREVIEW") {
            return runNetwork(effect, options.recoveryScope?.installationId);
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
            );
          }
          let record = attempts.current.get(operationId);
          if (record === undefined) {
            record = {
              ...scope,
              format: 1,
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
const previewDraft = (
  state: OperationState,
  dispatch: Dispatch,
  options: Options,
): Promise<void> => {
  try {
    const view = operationView(state);
    const prepared =
      view.prepared ??
      prepareRequest(
        "operation.execute",
        buildRequest(
          "operation.execute",
          assembleOperation(
            operationEntry(state.entry),
            options.normalizeValues?.(state.values, operationEntry(state.entry)) ?? state.values,
          ),
        ),
      );
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
    revalidateDisclosure: () => {
      if (
        !blocked &&
        state.disclosureRead === null &&
        options.subject === "CASE" &&
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
/** One pure state owner; refs contain only effect scheduling and mounted-lifetime controls. */
export const useOperation = (supplied: Options) => {
  const requester = useRequesterContext();
  const journal = useRecoveryJournal();
  const session = useContext(SessionContext);
  const contextEpoch = supplied.contextEpoch ?? session?.state.epoch ?? 0;
  const recoveryScope =
    supplied.recoveryScope ??
    (requester === null
      ? undefined
      : {
          serviceOrigin: location.origin,
          installationId: requester.installationId,
          actorId: requester.actorId,
        });
  const options: Options = {
    ...supplied,
    contextEpoch,
    journal: supplied.journal ?? journal,
    ...(recoveryScope === undefined ? {} : { recoveryScope }),
  };
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
  useEffects(state, dispatch, options);
  useLock(state, options.onLockChange);
  const view = operationView(state);
  const disclosureCurrent = hasCurrentDisclosure(state, options, contextEpoch);
  return {
    ...view,
    ...actions(state, dispatch, options),
    dirty: dirtyValues(state.values, state.baseline),
    disclosureCurrent,
    currentCase: disclosureCurrent ? state.caseObservation : null,
    revalidating: state.disclosureRead !== null,
    canRevalidateCase: options.subject === "CASE" && options.current !== null,
    review: disclosureCurrent ? view.review : null,
    receipt: disclosureCurrent ? view.receipt : null,
    contextChanged: state.target !== target,
  };
};
export type OperationModel = ReturnType<typeof useOperation>;
