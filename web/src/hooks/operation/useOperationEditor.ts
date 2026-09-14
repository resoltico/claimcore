import { useEffect, useReducer, useRef, useState } from "react";
import { isMutationUncertain, resultMessage, v2 } from "../../api/v2";
import {
  commandFor,
  commandInputs,
  correctionGroups,
  createDraft,
  isCorrectionGroupName,
  isDirty,
  prefilledValues,
  type CommandKind,
} from "../../domain/metadata";
import { initialOperation, operationReducer } from "../../domain/operationReducer";
import type {
  EditorActions,
  EditorMetadata,
  EditorState,
  OperationEditorModel,
  OperationEditorProps,
  SubmissionRequest,
} from "../../views/operation/editorTypes";
import {
  acceptedReceipt,
  isLocked,
  nextOperationId,
  prepared,
} from "../../views/operation/editorSupport";

const initialValues = (props: OperationEditorProps) =>
  prefilledValues(props.definition.definition, props.initialCommand, props.current?.case ?? null);

const useEditorState = (props: OperationEditorProps): EditorState => {
  const [initial] = useState(() =>
    initialOperation(
      nextOperationId(),
      props.initialCommand,
      initialValues(props),
      props.current?.case.fields.caseReference ?? "",
    ),
  );
  const [state, dispatch] = useReducer(operationReducer, initial);
  const [confirmed, setConfirmed] = useState(false);
  const [pendingCommand, setPendingCommand] = useState<CommandKind | null>(null);
  return {
    state,
    dispatch,
    confirmed,
    setConfirmed,
    pendingCommand,
    setPendingCommand,
  };
};

const draftForPrepare = (props: OperationEditorProps, state: EditorState) =>
  state.state.exposedRequest ??
  createDraft(
    state.state.operationId,
    state.state.caseReference,
    props.current?.case.revision ?? "0",
    state.state.command,
    { ...state.state.values },
  );

const dispatchPrepareResult = (
  state: EditorState,
  requestId: number,
  result: Awaited<ReturnType<typeof v2.prepare>>,
): void => {
  const value = result.kind === "outcome" ? prepared(result.value) : null;
  if (value !== null) {
    state.dispatch({
      type: "PREPARED",
      requestId,
      preparation: value.details,
      review: value.review,
    });
    return;
  }
  if (result.kind === "outcome" && result.value.outcome.tag === "OBSERVED_ACCEPTED") {
    state.dispatch({ type: "ACCEPTED", requestId, receipt: result.value.outcome.data.receipt });
    return;
  }
  if (result.kind === "outcome" && result.value.outcome.tag === "RETAINED_FOR_RECOVERY") {
    const { details, rejection } = result.value.outcome.data;
    state.dispatch({
      type: "RETAINED_FOR_RECOVERY",
      requestId,
      preparation: details,
      message: `${rejection.message} Inspect Recovery for this exact operation before taking another action.`,
    });
    return;
  }
  if (isMutationUncertain(result)) {
    state.dispatch({
      type: "PREPARATION_UNKNOWN",
      requestId,
      message: `${resultMessage(result)} Inspect Recovery before retrying this exact operation.`,
    });
    return;
  }
  const field =
    result.kind === "outcome" && result.value.outcome.tag === "REJECTED"
      ? result.value.outcome.data.rejection.field
      : null;
  state.dispatch({ type: "DEFINITELY_REJECTED", requestId, message: resultMessage(result), field });
};

const sendPrepare = async (
  props: OperationEditorProps,
  state: EditorState,
  requestId: number,
): Promise<void> => {
  const draft = draftForPrepare(props, state);
  state.dispatch({ type: "PREPARING", requestId, draft });
  const result = await v2.prepare(draft, props.token);
  dispatchPrepareResult(state, requestId, result);
};

const sendSubmit = async ({
  preparation,
  token,
  dispatch,
  requestId,
}: SubmissionRequest & { requestId: number }): Promise<void> => {
  const digest = preparation?.summary.requestSha256;
  if (preparation === null || typeof digest !== "string") return;
  dispatch({ type: "SUBMITTING", requestId });
  const result = await v2.submit(preparation.summary.operationId, digest, token);
  const receipt = result.kind === "outcome" ? acceptedReceipt(result.value) : null;
  if (receipt !== null) dispatch({ type: "ACCEPTED", requestId, receipt });
  else if (isMutationUncertain(result))
    dispatch({
      type: "OUTCOME_UNKNOWN",
      requestId,
      message: `${resultMessage(result)} Inspect Recovery before taking another action.`,
    });
  else
    dispatch({
      type: "DEFINITELY_REJECTED",
      requestId,
      message: resultMessage(result),
      field: null,
    });
};

const canSendPrepare = (state: EditorState): boolean =>
  state.state.delivery === "EDITING" ||
  state.state.delivery === "DEFINITELY_REJECTED" ||
  state.state.delivery === "PREPARATION_UNKNOWN";

const useRequestSerial = (): (() => number) => {
  const requestSerial = useRef(0);
  return (): number => ++requestSerial.current;
};

const changeActions = (
  props: OperationEditorProps,
  state: EditorState,
): Pick<
  EditorActions,
  "edit" | "editCorrection" | "setCorrectionMode" | "editReference" | "applyCommand"
> => {
  const edit = (field: string, value: string): void =>
    state.dispatch({ type: "EDIT", field, value, nextOperationId: nextOperationId() });
  const editCorrection = (group: string, field: string, value: string): void => {
    if (!isCorrectionGroupName(group)) throw new Error(`Unknown correction group ${group}.`);
    state.dispatch({
      type: "EDIT_CORRECTION",
      group,
      field,
      value,
      nextOperationId: nextOperationId(),
    });
  };
  const setCorrectionMode = (group: string, mode: string): void => {
    if (
      !isCorrectionGroupName(group) ||
      (mode !== "KEEP" && mode !== "REPLACE" && mode !== "CLEAR")
    )
      throw new Error("The generated correction descriptor contained an invalid action.");
    state.dispatch({
      type: "SET_CORRECTION_MODE",
      group,
      mode,
      nextOperationId: nextOperationId(),
    });
  };
  const editReference = (value: string): void =>
    state.dispatch({ type: "EDIT_REFERENCE", value, nextOperationId: nextOperationId() });
  const applyCommand = (command: CommandKind): void => {
    state.dispatch({
      type: "CHANGE_COMMAND",
      command,
      values: prefilledValues(props.definition.definition, command, props.current?.case ?? null),
      nextOperationId: nextOperationId(),
    });
    state.setPendingCommand(null);
  };
  return { edit, editCorrection, setCorrectionMode, editReference, applyCommand };
};

const useEditorActions = (props: OperationEditorProps, state: EditorState): EditorActions => {
  const preparing = useRef(false);
  const submitting = useRef(false);
  const nextRequestId = useRequestSerial();
  const changes = changeActions(props, state);
  const prepare = async (): Promise<void> => {
    if (preparing.current || !canSendPrepare(state)) return;
    preparing.current = true;
    try {
      await sendPrepare(props, state, nextRequestId());
    } finally {
      preparing.current = false;
    }
  };
  return {
    ...changes,
    prepare,
    submit: async () => {
      if (submitting.current || state.state.delivery !== "REVIEWING") return;
      submitting.current = true;
      try {
        await sendSubmit({
          preparation: state.state.preparation,
          token: props.token,
          dispatch: state.dispatch,
          requestId: nextRequestId(),
        });
      } finally {
        submitting.current = false;
      }
    },
  };
};

const metadata = (props: OperationEditorProps, state: EditorState): EditorMetadata => {
  const command = commandFor(props.definition.definition, state.state.command);
  const fields = commandInputs(props.definition.definition, state.state.command);
  const groups = correctionGroups(props.definition.definition, state.state.command);
  const referenceField = props.definition.definition.fields.find(
    (field) => field.name === "caseReference",
  );
  const currentReference = props.current?.case.fields.caseReference ?? "";
  return {
    command,
    fields,
    groups,
    referenceField,
    available: props.current?.availableCommands ?? [props.initialCommand],
    locked: isLocked(state.state.delivery),
    canPrepare:
      state.state.delivery === "EDITING" || state.state.delivery === "DEFINITELY_REJECTED",
    dirty: state.state.caseReference !== currentReference || isDirty(state.state.values),
    receipt: state.state.delivery === "ACCEPTED" ? state.state.receipt : null,
  };
};

export const useOperationEditor = (props: OperationEditorProps): OperationEditorModel => {
  const state = useEditorState(props);
  const details = metadata(props, state);
  const actions = useEditorActions(props, state);
  const { onMutationLockChange } = props;
  useEffect(() => onMutationLockChange(details.locked), [details.locked, onMutationLockChange]);
  useEffect(() => () => onMutationLockChange(false), [onMutationLockChange]);
  return { ...state, ...details, ...actions };
};
