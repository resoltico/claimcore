import type { Dispatch, SetStateAction } from "react";
import type {
  CommandDescriptor,
  CommandDraft,
  CorrectionGroupDescriptor,
  CurrentCase,
  DefinitionPayload,
  FieldDescriptor,
  PreparationDetails,
  Receipt,
} from "../../api/v3";
import type { OperationAction, OperationState } from "../../domain/operationReducer";
import type { CommandKind } from "../../domain/metadata";

import type { RecoveryTarget } from "../../domain/operationState";

export type OperationEditorProps = {
  token: string;
  definition: DefinitionPayload;
  current: CurrentCase | null;
  initialCommand: CommandKind;
  onClose: () => void;
  onCommitted: (receipt: Receipt) => void;
  onRecovery: (target: RecoveryTarget) => void;
  onMutationLockChange: (locked: boolean) => void;
};

export type EditorState = {
  state: OperationState;
  dispatch: Dispatch<OperationAction>;
  confirmed: boolean;
  setConfirmed: (value: boolean) => void;
  pendingDiscard: CommandKind | "LEAVE" | null;
  setPendingDiscard: Dispatch<SetStateAction<CommandKind | "LEAVE" | null>>;
};

export type EditorMetadata = {
  command: CommandDescriptor;
  fields: { field: FieldDescriptor }[];
  groups: ReadonlyArray<{
    group: CorrectionGroupDescriptor;
    fields: ReadonlyArray<FieldDescriptor>;
  }>;
  referenceField: FieldDescriptor | undefined;
  available: ReadonlyArray<CommandKind>;
  locked: boolean;
  canPrepare: boolean;
  dirty: boolean;
  receipt: Receipt | null;
};

export type EditorActions = {
  edit: (field: string, value: string) => void;
  editCorrection: (group: string, field: string, value: string) => void;
  setCorrectionMode: (group: string, mode: string) => void;
  editReference: (value: string) => void;
  applyCommand: (command: CommandKind) => void;
  prepare: () => Promise<void>;
  submit: () => Promise<void>;
};

export type OperationEditorModel = EditorState & EditorMetadata & EditorActions;

export type SubmissionRequest = {
  preparation: PreparationDetails | null;
  draft: CommandDraft | null;
  token: string;
  dispatch: Dispatch<OperationAction>;
  onRecovery: OperationEditorProps["onRecovery"];
};
