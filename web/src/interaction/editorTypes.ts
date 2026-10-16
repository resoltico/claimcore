import type { PreparedRequest } from "../protocol/preparedRequest";
import type { Dispatch, SetStateAction } from "react";
import type {
  CommandDescriptor,
  CorrectionGroupDescriptor,
  CurrentCase,
  ProductMetadata,
  FieldDescriptor,
  CaseReceipt,
} from "../api/service";
import type { OperationAction, OperationState } from "./operationState";
import type { CommandKind } from "./metadata";

export type OperationEditorProps = {
  token: string;
  definition: ProductMetadata;
  current: CurrentCase | null;
  initialCommand: CommandKind;
  onClose: () => void;
  onCommitted: (receipt: CaseReceipt) => void;
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
  canPreview: boolean;
  dirty: boolean;
  receipt: CaseReceipt | null;
};

export type EditorActions = {
  edit: (field: string, value: string) => void;
  editCorrection: (group: string, field: string, value: string) => void;
  setCorrectionMode: (group: string, mode: string) => void;
  editReference: (value: string) => void;
  applyCommand: (command: CommandKind) => void;
  preview: () => Promise<void>;
  submit: () => Promise<void>;
};

export type OperationEditorModel = EditorState & EditorMetadata & EditorActions;

export type SubmissionRequest = {
  prepared: PreparedRequest<"case.execute"> | null;
  token: string;
  dispatch: Dispatch<OperationAction>;
};
