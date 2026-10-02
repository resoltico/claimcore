import { usePreparedConsent } from "./usePreparedConsent";
import { sendPrepare, sendSubmit } from "./operationDelivery";
import { useEffect, useReducer, useRef, useState } from "react";
import {
  commandFor,
  commandInputs,
  correctionGroups,
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
} from "../../views/operation/editorTypes";
import { isLocked, nextOperationId } from "../../views/operation/editorSupport";

const initialValues = (props: OperationEditorProps) =>
  prefilledValues(props.definition.definition, props.initialCommand, props.current?.case ?? null);

const createInitialOperation = (props: OperationEditorProps) =>
  initialOperation(
    nextOperationId(),
    props.initialCommand,
    initialValues(props),
    props.current?.case.fields.caseReference ?? "",
  );

const useEditorState = (props: OperationEditorProps): EditorState => {
  const [state, dispatch] = useReducer(operationReducer, props, createInitialOperation);
  const { confirmed, setConfirmed } = usePreparedConsent(state.preparation);
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
  const edit = (field: string, value: string): void => {
    state.dispatch({ type: "EDIT", field, value, nextOperationId: nextOperationId() });
  };
  const editCorrection = (group: string, field: string, value: string): void => {
    if (!isCorrectionGroupName(group)) {
      throw new Error(`Unknown correction group ${group}.`);
    }
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
    ) {
      throw new Error("The generated correction descriptor contained an invalid action.");
    }
    state.dispatch({
      type: "SET_CORRECTION_MODE",
      group,
      mode,
      nextOperationId: nextOperationId(),
    });
  };
  const editReference = (value: string): void => {
    state.dispatch({ type: "EDIT_REFERENCE", value, nextOperationId: nextOperationId() });
  };
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
    if (preparing.current || !canSendPrepare(state)) {
      return;
    }
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
      if (submitting.current || !state.confirmed || state.state.delivery !== "REVIEWING") {
        return;
      }
      submitting.current = true;
      try {
        await sendSubmit({
          preparation: state.state.preparation,
          draft: state.state.exposedRequest,
          token: props.token,
          dispatch: state.dispatch,
          onRecovery: props.onRecovery,
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
  useEffect(() => {
    onMutationLockChange(details.locked);
  }, [details.locked, onMutationLockChange]);
  useEffect(
    () => () => {
      onMutationLockChange(false);
    },
    [onMutationLockChange],
  );
  return { ...state, ...details, ...actions };
};
