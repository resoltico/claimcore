import type { Notice } from "../api/notices";
import type { Review, CommandDraft, Receipt } from "../api/service";
import type { CommandKind, CorrectionGroupName, CorrectionMode, DraftValues } from "./metadata";

export type DeliveryState =
  | "EDITING"
  | "PREVIEWING"
  | "REVIEWING"
  | "SUBMITTING"
  | "ACCEPTED"
  | "DEFINITELY_REJECTED"
  | "OUTCOME_UNKNOWN";
export type OperationState = {
  delivery: DeliveryState;
  operationId: string;
  caseReference: string;
  command: CommandKind;
  values: DraftValues;
  requestSha256: string | null;
  requestBytes: string | null;
  review: Review | null;
  receipt: Receipt | null;
  message: Notice | null;
  fieldError: { name: string; message: Notice } | null;
  exposedRequest: CommandDraft | null;
  pending: { kind: "PREVIEW" | "SUBMIT"; requestId: number } | null;
};
export type OperationAction =
  | { type: "EDIT"; field: string; value: string; nextOperationId: string }
  | {
      type: "EDIT_CORRECTION";
      group: CorrectionGroupName;
      field: string;
      value: string;
      nextOperationId: string;
    }
  | {
      type: "SET_CORRECTION_MODE";
      group: CorrectionGroupName;
      mode: CorrectionMode;
      nextOperationId: string;
    }
  | { type: "EDIT_REFERENCE"; value: string; nextOperationId: string }
  | { type: "CHANGE_COMMAND"; command: CommandKind; values: DraftValues; nextOperationId: string }
  | { type: "PREVIEWING"; requestId: number; draft: CommandDraft }
  | { type: "REVIEWED"; requestId: number; requestSha256: string; review: Review }
  | { type: "DEFINITELY_REJECTED"; requestId: number; message: Notice; field: string | null }
  | { type: "SUBMITTING"; requestId: number }
  | { type: "ACCEPTED"; requestId: number; receipt: Receipt; message?: Notice | null }
  | { type: "OUTCOME_UNKNOWN"; requestId: number; message: Notice }
  | { type: "RETURN_TO_EDIT" }
  | { type: "RESET_MESSAGE" };
