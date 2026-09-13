import type { Dispatch, SetStateAction } from "react";
import type { CurrentCase, DefinitionPayload, PreparationDetails, Receipt } from "../../api/v2";
import type { CommandDescriptor, FieldDescriptor } from "../../api/v2";
import type { OperationAction, OperationState } from "../../domain/operationReducer";
import type { CommandKind } from "../../domain/metadata";

export type OperationEditorProps = {
  token: string;
  definition: DefinitionPayload;
  current: CurrentCase | null;
  initialCommand: CommandKind;
  onClose: () => void;
  onCommitted: () => void;
  onMutationLockChange: (locked: boolean) => void;
};

export type EditorState = {
  state: OperationState;
  dispatch: Dispatch<OperationAction>;
  confirmed: boolean;
  setConfirmed: Dispatch<SetStateAction<boolean>>;
  pendingCommand: CommandKind | null;
  setPendingCommand: Dispatch<SetStateAction<CommandKind | null>>;
};

export type EditorMetadata = {
  command: CommandDescriptor;
  fields: { field: FieldDescriptor }[];
  referenceField: FieldDescriptor | undefined;
  available: ReadonlyArray<CommandKind>;
  locked: boolean;
  canPrepare: boolean;
  dirty: boolean;
  receipt: Receipt | null;
};

export type EditorActions = {
  edit: (field: string, value: string) => void;
  editReference: (value: string) => void;
  applyCommand: (command: CommandKind) => void;
  prepare: () => Promise<void>;
  submit: () => Promise<void>;
};

export type OperationEditorModel = EditorState & EditorMetadata & EditorActions;

export type SubmissionRequest = {
  preparation: PreparationDetails | null;
  token: string;
  dispatch: Dispatch<OperationAction>;
};
