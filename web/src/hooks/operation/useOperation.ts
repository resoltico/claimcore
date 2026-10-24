import { useEffect, useLayoutEffect, useReducer, useRef } from "react";
import {
  service,
  resultNotice,
  type CurrentCase,
  type ApiResult,
  type ServiceResponse,
  type OperationReceipt,
} from "../../api/service";
import type { RecoveryItem } from "@contracts/client";
import { isDisclosureRefused } from "../../api/outcomes";
import { mutationUncertain } from "../../protocol/delivery";
import { prepareRequest, ProtocolInputError } from "../../protocol/preparedRequest";
import { buildRequest } from "../../protocol/client";
import type { FormValues } from "../../protocol/forms";
import { initialOperationValues, assembleOperation } from "../../interaction/operationForm";
import {
  operationReducer,
  operationView,
  operationSource,
  recoveryTarget,
  type OperationState,
  type OperationEffect,
  type OperationAction,
  type EffectIdentity,
} from "../../interaction/operationState";
import type { Notice } from "../../api/notices";

type Options = {
  scope: "CASE" | "AUTHORITY";
  kind: string;
  current: CurrentCase | null;
  token: string;
  onLockChange: (locked: boolean) => void;
  onRecorded?: (receipt: OperationReceipt) => void;
  onDisclosureRefused?: (notice: Notice) => void;
};
const targetKey = (options: Options): string =>
  `${options.scope}:${options.current?.caseReference.toUpperCase() ?? "NEW"}`;
const fresh = (options: Options, kind = options.kind): OperationState => {
  const values = initialOperationValues(options.scope, kind, options.current);
  return {
    target: targetKey(options),
    context: { token: options.token, target: targetKey(options) },
    disclosureToken: options.token,
    disclosureRefused: false,
    values,
    baseline: values,
    generation: 0,
    operation: { tag: "EDITING", exposed: null },
    effects: [],
    notice: null,
    fieldError: null,
    pendingDiscard: null,
  };
};
const activeTarget = (state: OperationState, options: Options): string => {
  const source = operationSource(state.operation);
  return source?.kind === "RECOVERED" ? recoveryTarget(source.item) : targetKey(options);
};
const identity = (options: Options, state: OperationState): EffectIdentity => ({
  id: crypto.randomUUID(),
  token: options.token,
  target: activeTarget(state, options),
});
const dirtyValues = (left: FormValues, right: FormValues): boolean =>
  Object.keys(left).length !== Object.keys(right).length ||
  Object.entries(left).some(([key, value]) => right[key] !== value);
type Reply = ApiResult<ServiceResponse<"operation.execute"> | ServiceResponse<"operation.preview">>;
const completion = (
  effect: Extract<OperationEffect, { kind: "PREVIEW" | "EXECUTE" }>,
  result: Reply,
): OperationAction => {
  const recoverable =
    result.kind === "outcome" && "recoveryId" in result.value ? result.value.recoveryId : null;
  if (result.kind === "outcome") {
    const { outcome } = result.value;
    if (outcome.tag === "ACCEPTED" || outcome.tag === "ALREADY_ACCEPTED") {
      return {
        type: "RECORD",
        receipt: outcome.data,
        identity: effect.identity,
        recoveryId: recoverable,
      };
    }
    if (outcome.tag === "REVIEWED") {
      return { type: "REVIEW", review: outcome.data.review, identity: effect.identity };
    }
  }
  return {
    type: "REFUSE",
    identity: effect.identity,
    notice: resultNotice(result),
    target:
      result.kind === "outcome" && result.value.outcome.tag === "REJECTED"
        ? result.value.outcome.data.target
        : null,
    uncertain: effect.kind === "EXECUTE" && mutationUncertain(result),
    disclosure: isDisclosureRefused(result),
    recoveryId: recoverable,
  };
};
const recoveredReceiptMatches = (item: RecoveryItem, result: Reply): boolean => {
  if (
    result.kind !== "outcome" ||
    (result.value.outcome.tag !== "ACCEPTED" && result.value.outcome.tag !== "ALREADY_ACCEPTED")
  ) {
    return true;
  }
  const receipt = result.value.outcome.data;
  if (
    receipt.operationId !== item.operationId ||
    receipt.scope.kind !== item.scope ||
    receipt.scope.act !== item.act
  ) {
    return false;
  }
  return receipt.scope.kind === "CASE"
    ? receipt.scope.caseReference.toUpperCase() === item.caseReference?.toUpperCase() &&
        receipt.scope.revision === (item.expectedRevision ?? -1) + 1
    : receipt.scope.targetActorId === item.targetActorId;
};
const runNetwork = async (
  effect: Extract<OperationEffect, { kind: "PREVIEW" | "EXECUTE" }>,
): Promise<OperationAction> => {
  if (effect.kind === "PREVIEW") {
    return completion(effect, await service.preview(effect.source.prepared, effect.identity.token));
  }
  if (effect.source.kind === "LOCAL") {
    return completion(effect, await service.execute(effect.source.prepared, effect.identity.token));
  }
  const { item } = effect.source;
  const result = await service.retry(item.recoveryId, item.operationId, effect.identity.token);
  return completion(
    effect,
    recoveredReceiptMatches(item, result)
      ? result
      : { kind: "deliveryFailure", notice: { kind: "local", reason: "incomplete" } },
  );
};
const useEffects = (
  state: OperationState,
  dispatch: React.Dispatch<OperationAction>,
  options: Options,
) => {
  const started = useRef(new Set<string>());
  const alive = useRef(true);
  useEffect(() => {
    alive.current = true;
    return () => {
      alive.current = false;
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
      dispatch({ type: "EFFECT_STARTED", id: effect.identity.id });
      if (
        effect.identity.token !== options.token ||
        effect.identity.target !== activeTarget(state, options)
      ) {
        continue;
      }
      if (effect.kind === "RECORDED") {
        options.onRecorded?.(effect.receipt);
      } else if (effect.kind === "DISCLOSURE_REFUSED") {
        options.onDisclosureRefused?.(effect.notice);
      } else if (effect.kind === "FORGET") {
        void service.forget(effect.recoveryId, effect.identity.token);
      } else {
        void runNetwork(effect).then((action) => {
          if (alive.current) {
            dispatch(action);
          }
        });
      }
    }
  }, [state, dispatch, options]);
};
const useLock = (state: OperationState, onLockChange: Options["onLockChange"]) => {
  const locked =
    state.operation.tag !== "ACCEPTED" &&
    (operationSource(state.operation) !== null || dirtyValues(state.values, state.baseline));
  useEffect(() => {
    onLockChange(locked);
    return () => {
      onLockChange(false);
    };
  }, [locked, onLockChange]);
};
type Dispatch = React.Dispatch<OperationAction>;
const acknowledge = (state: OperationState, options: Options) => {
  const id = identity(options, state);
  const next = operationReducer(state, { type: "ACKNOWLEDGE", identity: id });
  const effect = next.effects.find(
    (value) => value.kind === "FORGET" && value.identity.id === id.id,
  );
  if (effect?.kind === "FORGET") {
    void service.forget(effect.recoveryId, effect.identity.token);
  }
};
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
        buildRequest("operation.execute", assembleOperation(state.values)),
      );
    dispatch({
      type: "PREVIEW",
      prepared,
      identity: identity(options, state),
      generation: state.generation,
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
const editing = (state: OperationState, dispatch: Dispatch, options: Options) => {
  const view = operationView(state);
  const blocked =
    view.busy ||
    view.priorUncertain ||
    view.phase === "ACCEPTED" ||
    state.target !== targetKey(options);
  return {
    edit: (path: string, value: string | boolean) => {
      if (!blocked) {
        if (state.operation.tag === "EDITING" && state.operation.exposed?.recoveryId !== null) {
          acknowledge(state, options);
        }
        dispatch({ type: "EDIT", path, value, id: crypto.randomUUID() });
      }
    },
    returnToEdit: () => {
      if (!blocked) {
        dispatch({ type: "RETURN" });
      }
    },
    confirm: (value: boolean) => {
      if (!blocked) {
        dispatch({ type: "CONFIRM", value });
      }
    },
    discard: (acknowledgedUncertainty = false, kind = options.kind) => {
      if (!view.busy && (!view.priorUncertain || acknowledgedUncertainty)) {
        if (!view.priorUncertain) {
          acknowledge(state, options);
        }
        dispatch({ type: "RESET", state: fresh(options, kind), abandon: acknowledgedUncertainty });
      }
    },
    preview: () => (blocked ? Promise.resolve() : previewDraft(state, dispatch, options)),
  };
};
const actions = (state: OperationState, dispatch: Dispatch, options: Options) => ({
  ...editing(state, dispatch, options),
  acknowledge: () => {
    acknowledge(state, options);
  },
  requestDiscard: (kind: string | null) => {
    dispatch({ type: "DISCARD", kind });
  },
  restoreDisclosure: () => {
    dispatch({ type: "RESTORE" });
  },
  invalidateDisclosure: () => {
    dispatch({ type: "INVALIDATE", refused: true });
  },
  recover: (item: RecoveryItem) => {
    dispatch({ type: "RECOVER", item });
  },
  execute: () => {
    dispatch({ type: "EXECUTE", identity: identity(options, state) });
    return Promise.resolve();
  },
});
/** One pure state owner; refs contain only effect scheduling and mounted-lifetime controls. */
export const useOperation = (options: Options) => {
  const [state, dispatch] = useReducer(operationReducer, options, fresh);
  const target = activeTarget(state, options);
  useLayoutEffect(() => {
    dispatch({ type: "CONTEXT", token: options.token, target });
  }, [options.token, target]);
  useEffects(state, dispatch, options);
  useLock(state, options.onLockChange);
  const view = operationView(state);
  const disclosureCurrent =
    state.disclosureToken === options.token && state.target === target && !state.disclosureRefused;
  return {
    ...view,
    ...actions(state, dispatch, options),
    dirty: dirtyValues(state.values, state.baseline),
    disclosureCurrent,
    review: disclosureCurrent ? view.review : null,
    receipt: disclosureCurrent ? view.receipt : null,
    contextChanged: state.target !== target,
  };
};
export type OperationModel = ReturnType<typeof useOperation>;
