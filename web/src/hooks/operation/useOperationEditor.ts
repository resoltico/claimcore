import { useReviewConsent } from "./useReviewConsent";
import { sendPreview, sendSubmit } from "./operationDelivery";
import { useEffect, useReducer, useRef, useState } from "react";
import {
  commandFor,
  commandInputs,
  correctionGroups,
  isCorrectionGroupName,
  isDirty,
  isCaseAct,
  prefilledValues,
  type CommandKind,
} from "../../interaction/metadata";
import { editable, initialOperation, operationReducer } from "../../interaction/operationReducer";
import type {
  EditorActions,
  EditorMetadata,
  EditorState,
  OperationEditorModel,
  OperationEditorProps,
} from "../../interaction/editorTypes";
import { isLocked, nextOperationId } from "../../interaction/editorSupport";

const initialValues = (props: OperationEditorProps) =>
  prefilledValues(props.definition.descriptors, props.initialCommand, props.current ?? null);

const createInitialOperation = (props: OperationEditorProps) =>
  initialOperation(
    nextOperationId(),
    props.initialCommand,
    initialValues(props),
    props.current?.caseReference ?? "",
  );

const useEditorState = (props: OperationEditorProps): EditorState => {
  const [state, dispatch] = useReducer(operationReducer, props, createInitialOperation);
  const { confirmed, setConfirmed } = useReviewConsent(state.review, state.requestBytes);
  const [pendingDiscard, setPendingDiscard] = useState<CommandKind | "LEAVE" | null>(null);
  return {
    state,
    dispatch,
    confirmed,
    setConfirmed,
    pendingDiscard,
    setPendingDiscard,
  };
};

const canSendPreview = (state: EditorState): boolean => editable(state.state);

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
      values: prefilledValues(props.definition.descriptors, command, props.current ?? null),
      nextOperationId: nextOperationId(),
    });
    state.setPendingDiscard(null);
  };
  return { edit, editCorrection, setCorrectionMode, editReference, applyCommand };
};

const useEditorActions = (props: OperationEditorProps, state: EditorState): EditorActions => {
  const preparing = useRef(false);
  const submitting = useRef(false);
  const nextRequestId = useRequestSerial();
  const changes = changeActions(props, state);
  const preview = async (): Promise<void> => {
    if (preparing.current || !canSendPreview(state)) {
      return;
    }
    preparing.current = true;
    try {
      await sendPreview(props, state, nextRequestId());
    } finally {
      preparing.current = false;
    }
  };
  return {
    ...changes,
    preview,
    submit: async () => {
      if (
        submitting.current ||
        (state.state.delivery !== "OUTCOME_UNKNOWN" &&
          (!state.confirmed || state.state.delivery !== "REVIEWING"))
      ) {
        return;
      }
      submitting.current = true;
      try {
        await sendSubmit({
          prepared: state.state.prepared,
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
  const command = commandFor(props.definition.descriptors, state.state.command);
  const fields = commandInputs(props.definition.descriptors, state.state.command);
  const commandOffer = props.current?.advice.acts.find((item) => item.act === state.state.command);
  const groups = correctionGroups(props.definition.descriptors, state.state.command).map(
    ({ group, fields: groupFields }) => {
      const choices =
        commandOffer !== undefined && "correction" in commandOffer
          ? commandOffer.correction[group.name]
          : undefined;
      return {
        group: {
          ...group,
          actions: group.actions.filter(
            (action) => choices === undefined || choices.includes(action),
          ),
        },
        fields: groupFields,
      };
    },
  );
  const referenceField = props.definition.descriptors.fields.find(
    (field) => field.name === "caseReference",
  );
  const currentReference = props.current?.caseReference ?? "";
  return {
    command,
    fields,
    groups,
    referenceField,
    available: props.current?.advice.acts.map((offer) => offer.act).filter(isCaseAct) ?? [
      props.initialCommand,
    ],
    locked: isLocked(state.state.delivery),
    canPreview: editable(state.state),
    dirty:
      state.state.caseReference !== currentReference ||
      isDirty(
        state.state.values,
        prefilledValues(props.definition.descriptors, state.state.command, props.current ?? null),
      ),
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
