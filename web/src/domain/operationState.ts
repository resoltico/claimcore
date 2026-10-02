import type { Notice } from "../api/notices";
import type { AdvisoryReview, CommandDraft, PreparationDetails, Receipt } from "../api/v3";
import type { CommandKind, CorrectionGroupName, CorrectionMode, DraftValues } from "./metadata";

export type RecoveryTarget = { operationId: string; requestSha256: string | null; message: Notice };

export type DeliveryState =
  | "EDITING"
  | "PREPARING"
  | "PREPARATION_UNKNOWN"
  | "RETAINED_FOR_RECOVERY"
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
  preparation: PreparationDetails | null;
  review: AdvisoryReview | null;
  receipt: Receipt | null;
  message: Notice | null;
  fieldError: { name: string; message: Notice } | null;
  exposedRequest: CommandDraft | null;
  pending: { kind: "PREPARE" | "SUBMIT"; requestId: number } | null;
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
  | { type: "PREPARING"; requestId: number; draft: CommandDraft }
  | { type: "PREPARED"; requestId: number; preparation: PreparationDetails; review: AdvisoryReview }
  | { type: "PREPARATION_UNKNOWN"; requestId: number; message: Notice }
  | {
      type: "RETAINED_FOR_RECOVERY";
      requestId: number;
      preparation: PreparationDetails;
      message: Notice;
    }
  | { type: "DEFINITELY_REJECTED"; requestId: number; message: Notice; field: string | null }
  | { type: "SUBMITTING"; requestId: number }
  | { type: "ACCEPTED"; requestId: number; receipt: Receipt; message?: Notice | null }
  | { type: "OUTCOME_UNKNOWN"; requestId: number; message: Notice }
  | { type: "KEEP_FOR_RECOVERY" }
  | { type: "RESET_MESSAGE" };
