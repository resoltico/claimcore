import { useEffect, useReducer, useRef, useState } from "react";
import { isMutationUncertain, resultMessage, v2 } from "../../api/v2";
import {
  commandFor,
  commandInputs,
  createDraft,
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

const sendPrepare = async (props: OperationEditorProps, state: EditorState): Promise<void> => {
  state.dispatch({ type: "PREPARING" });
  const draft = createDraft(
    state.state.operationId,
    state.state.caseReference,
    props.current?.case.revision ?? "0",
    state.state.command,
    state.state.values,
  );
  const result = await v2.prepare(draft, props.token);
  const value = result.kind === "outcome" ? prepared(result.value) : null;
  if (value !== null)
    state.dispatch({ type: "PREPARED", preparation: value.details, review: value.review });
  else if (result.kind === "outcome" && result.value.outcome.tag === "OBSERVED_ACCEPTED")
    state.dispatch({ type: "ACCEPTED", receipt: result.value.outcome.data.receipt });
  else if (result.kind === "outcome" && result.value.outcome.tag === "RETAINED_FOR_RECOVERY") {
    const { details, rejection } = result.value.outcome.data;
    state.dispatch({
      type: "RETAINED_FOR_RECOVERY",
      preparation: details,
      message: `${rejection.message} Inspect Recovery for this exact operation before taking another action.`,
    });
  } else if (isMutationUncertain(result))
    state.dispatch({
      type: "PREPARATION_UNKNOWN",
      message: `${resultMessage(result)} Inspect Recovery before retrying this exact operation.`,
    });
  else {
    const field =
      result.kind === "outcome" && result.value.outcome.tag === "REJECTED"
        ? result.value.outcome.data.rejection.field
        : null;
    state.dispatch({ type: "DEFINITELY_REJECTED", message: resultMessage(result), field });
  }
};

const sendSubmit = async ({ preparation, token, dispatch }: SubmissionRequest): Promise<void> => {
  const digest = preparation?.summary.requestSha256;
  if (preparation === null || typeof digest !== "string") return;
  dispatch({ type: "SUBMITTING" });
  const result = await v2.submit(preparation.summary.operationId, digest, token);
  const receipt = result.kind === "outcome" ? acceptedReceipt(result.value) : null;
  if (receipt !== null) dispatch({ type: "ACCEPTED", receipt });
  else if (isMutationUncertain(result))
    dispatch({
      type: "OUTCOME_UNKNOWN",
      message: `${resultMessage(result)} Inspect Recovery before taking another action.`,
    });
  else dispatch({ type: "DEFINITELY_REJECTED", message: resultMessage(result), field: null });
};

const useEditorActions = (props: OperationEditorProps, state: EditorState): EditorActions => {
  const preparing = useRef(false);
  const edit = (field: string, value: string): void =>
    state.dispatch({ type: "EDIT", field, value, nextOperationId: nextOperationId() });
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
  const prepare = async (): Promise<void> => {
    if (preparing.current) return;
    preparing.current = true;
    try {
      await sendPrepare(props, state);
    } finally {
      preparing.current = false;
    }
  };
  return {
    edit,
    editReference,
    applyCommand,
    prepare,
    submit: () =>
      sendSubmit({
        preparation: state.state.preparation,
        token: props.token,
        dispatch: state.dispatch,
      }),
  };
};

const metadata = (props: OperationEditorProps, state: EditorState): EditorMetadata => {
  const command = commandFor(props.definition.definition, state.state.command);
  const fields = commandInputs(props.definition.definition, state.state.command);
  const referenceField = props.definition.definition.fields.find(
    (field) => field.name === "caseReference",
  );
  const currentReference = props.current?.case.fields.caseReference ?? "";
  return {
    command,
    fields,
    referenceField,
    available: props.current?.availableCommands ?? [props.initialCommand],
    locked: isLocked(state.state.delivery),
    canPrepare:
      state.state.delivery === "EDITING" || state.state.delivery === "DEFINITELY_REJECTED",
    dirty:
      state.state.caseReference !== currentReference ||
      Object.values(state.state.values).some((value) => value !== ""),
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
