import { useEffect, useLayoutEffect, useReducer, useRef } from "react";
import {
  service,
  resultNotice,
  type CurrentCase,
  type ApiResult,
  type ServiceResponse,
  type OperationReceipt,
} from "../../api/service";
import { isDisclosureRefused } from "../../api/outcomes";
import { mutationAccepted, mutationUncertain } from "../../protocol/delivery";
import { prepareRequest, ProtocolInputError } from "../../protocol/preparedRequest";
import { buildRequest } from "../../protocol/client";
import { formValue, type FormValues } from "../../protocol/forms";
import { initialOperationValues, operationDocument } from "../../interaction/operationForm";
import {
  operationReducer,
  type OperationAction,
  type OperationState,
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
    disclosureToken: options.token,
    disclosureRefused: false,
    phase: "EDITING",
    values,
    baseline: values,
    prepared: null,
    priorUncertain: false,
    review: null,
    receipt: null,
    notice: null,
    fieldError: null,
    confirmed: false,
  };
};
const useStateControl = (options: Options) => {
  const [state, update] = useReducer(operationReducer, options, fresh);
  const latest = useRef(state);
  const renderedContext = useRef({ token: options.token, target: targetKey(options) });
  const control = useRef({
    serial: 0,
    busy: false,
    mutating: false,
    token: options.token,
    alive: true,
  });
  const dispatch = (action: OperationAction) => {
    latest.current = operationReducer(latest.current, action);
    update(action);
  };
  useLayoutEffect(() => {
    latest.current = state;
    renderedContext.current = { token: options.token, target: targetKey(options) };
    if (control.current.token !== options.token) {
      const uncertain = control.current.mutating;
      control.current.token = options.token;
      control.current.serial += 1;
      control.current.busy = false;
      control.current.mutating = false;
      dispatch({ type: "INVALIDATE", uncertain, refused: false });
    }
  });
  useEffect(() => {
    const value = control.current;
    value.alive = true;
    return () => {
      value.alive = false;
      value.serial += 1;
    };
  }, []);
  const reset = (kind = options.kind) => {
    const next = fresh(options, kind);
    dispatch({
      type: "RESET",
      state: { ...next, disclosureToken: latest.current.disclosureToken },
    });
  };
  return { state, dispatch, latest, control, renderedContext, reset };
};
type Model = ReturnType<typeof useStateControl>;
const dirtyValues = (left: FormValues, right: FormValues): boolean =>
  Object.keys(left).length !== Object.keys(right).length ||
  Object.entries(left).some(([key, value]) => right[key] !== value);
const useLock = (model: Model, onLockChange: Options["onLockChange"]) => {
  const { state } = model;
  const locked =
    state.phase !== "ACCEPTED" &&
    (state.prepared !== null || state.priorUncertain || dirtyValues(state.values, state.baseline));
  useEffect(() => {
    onLockChange(locked);
    return () => {
      onLockChange(false);
    };
  }, [locked, onLockChange]);
};
const sameContext = (model: Model, options: Options): boolean =>
  model.renderedContext.current.token === options.token &&
  model.renderedContext.current.target === targetKey(options);
const complete = (model: Model, serial: number, options: Options): boolean => {
  const control = model.control.current;
  if (!control.alive || control.serial !== serial) {
    return false;
  }
  if (sameContext(model, options)) {
    return true;
  }
  model.dispatch({ type: "INVALIDATE", uncertain: control.mutating, refused: false });
  control.busy = false;
  control.mutating = false;
  return false;
};
const begin = (model: Model, mutating: boolean): number => {
  const control = model.control.current;
  control.busy = true;
  control.mutating = mutating;
  return ++control.serial;
};
const prepare = (model: Model) => {
  const state = model.latest.current;
  try {
    return (
      state.prepared ??
      prepareRequest(
        "operation.execute",
        buildRequest(
          "operation.execute",
          formValue(operationDocument, operationDocument, "", state.values),
        ),
      )
    );
  } catch (error) {
    model.dispatch({
      type: "INVALID_INPUT",
      path: error instanceof ProtocolInputError ? error.path.join(".") : null,
    });
    return null;
  }
};
const refused = (
  model: Model,
  options: Options,
  result:
    | ApiResult<ServiceResponse<"operation.execute">>
    | ApiResult<ServiceResponse<"operation.preview">>,
  uncertain: boolean,
) => {
  const notice = resultNotice(result);
  const disclosure = isDisclosureRefused(result);
  if (disclosure) {
    options.onDisclosureRefused?.(notice);
  }
  const target =
    result.kind === "outcome" && result.value.outcome.tag === "REJECTED"
      ? result.value.outcome.data.target
      : null;
  model.dispatch({ type: "REFUSE", notice, target, uncertain, disclosure });
};
const preview = async (model: Model, options: Options): Promise<void> => {
  const state = model.latest.current;
  if (
    model.control.current.busy ||
    state.priorUncertain ||
    state.phase === "ACCEPTED" ||
    state.target !== targetKey(options)
  ) {
    return;
  }
  const prepared = prepare(model);
  if (prepared === null) {
    return;
  }
  const serial = begin(model, false);
  model.dispatch({ type: "PREVIEW", prepared });
  const result = await service.preview(prepared, options.token);
  if (!complete(model, serial, options)) {
    return;
  }
  model.control.current.busy = false;
  if (result.kind === "outcome" && result.value.outcome.tag === "REVIEWED") {
    model.dispatch({
      type: "REVIEW",
      review: result.value.outcome.data.review,
      token: options.token,
    });
  } else if (result.kind === "outcome" && result.value.outcome.tag === "ALREADY_ACCEPTED") {
    model.dispatch({ type: "RECORD", receipt: result.value.outcome.data, token: options.token });
    options.onRecorded?.(result.value.outcome.data);
  } else {
    refused(model, options, result, false);
  }
};
const reviewed = (state: OperationState, options: Options): boolean =>
  state.phase === "REVIEWING" &&
  state.review !== null &&
  !state.disclosureRefused &&
  state.disclosureToken === options.token &&
  state.target === targetKey(options);
const mayExecute = (model: Model, options: Options): boolean => {
  const state = model.latest.current;
  return (
    !model.control.current.busy &&
    state.prepared !== null &&
    (state.priorUncertain || (state.confirmed && reviewed(state, options)))
  );
};
const execute = async (model: Model, options: Options): Promise<void> => {
  const state = model.latest.current;
  if (!mayExecute(model, options) || state.prepared === null) {
    return;
  }
  const serial = begin(model, true);
  model.dispatch({ type: "EXECUTE" });
  const result = await service.execute(state.prepared, options.token);
  if (!complete(model, serial, options)) {
    return;
  }
  model.control.current.busy = false;
  model.control.current.mutating = false;
  if (
    result.kind === "outcome" &&
    (result.value.outcome.tag === "ACCEPTED" || result.value.outcome.tag === "ALREADY_ACCEPTED")
  ) {
    model.dispatch({ type: "RECORD", receipt: result.value.outcome.data, token: options.token });
    options.onRecorded?.(result.value.outcome.data);
  } else {
    refused(
      model,
      options,
      result,
      mutationUncertain(result) || (state.priorUncertain && !mutationAccepted(result)),
    );
  }
};
const editing = (model: Model, options: Options) => {
  const blocked = () =>
    model.control.current.busy ||
    model.latest.current.priorUncertain ||
    model.latest.current.phase === "ACCEPTED" ||
    model.latest.current.target !== targetKey(options);
  return {
    edit: (path: string, value: string | boolean) => {
      const state = model.latest.current;
      if (!blocked() && state.values[path] !== value) {
        model.dispatch({
          type: "EDIT",
          path,
          value,
          id: state.prepared === null ? String(state.values["operationId"]) : crypto.randomUUID(),
        });
      }
    },
    returnToEdit: () => {
      if (!blocked()) {
        model.dispatch({ type: "RETURN" });
      }
    },
    confirm: (value: boolean) => {
      if (
        !model.control.current.busy &&
        !model.latest.current.priorUncertain &&
        reviewed(model.latest.current, options)
      ) {
        model.dispatch({ type: "CONFIRM", value });
      }
    },
    discard: (acknowledgedUncertainty = false, kind = options.kind) => {
      if (
        !model.control.current.busy &&
        (!model.latest.current.priorUncertain || acknowledgedUncertainty)
      ) {
        model.reset(kind);
      }
    },
  };
};
const disclosureActions = (model: Model) => ({
  restoreDisclosure: () => {
    if (model.latest.current.disclosureRefused) {
      model.dispatch({ type: "INVALIDATE", uncertain: false, refused: false });
    }
  },
  invalidateDisclosure: () => {
    const control = model.control.current;
    model.dispatch({ type: "INVALIDATE", uncertain: control.mutating, refused: true });
    control.serial += 1;
    control.busy = false;
    control.mutating = false;
  },
});
/** Both scopes share the reducer, frozen bytes, prior uncertainty and consent boundary. */
export const useOperation = (options: Options) => {
  const model = useStateControl(options);
  useLock(model, options.onLockChange);
  const disclosureCurrent =
    model.state.disclosureToken === options.token &&
    model.state.target === targetKey(options) &&
    !model.state.disclosureRefused;
  return {
    ...model.state,
    busy: model.state.phase === "PREVIEWING" || model.state.phase === "SUBMITTING",
    dirty: dirtyValues(model.state.values, model.state.baseline),
    ...editing(model, options),
    ...disclosureActions(model),
    preview: () => preview(model, options),
    execute: () => execute(model, options),
    disclosureCurrent,
    review: disclosureCurrent ? model.state.review : null,
    receipt: disclosureCurrent ? model.state.receipt : null,
    contextChanged: model.state.target !== targetKey(options),
  };
};
export type OperationModel = ReturnType<typeof useOperation>;
