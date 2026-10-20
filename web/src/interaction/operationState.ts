import type { MutationLifecycle } from "./mutationLifecycle";
import type { ResolutionEvidence } from "../protocol/delivery";
import type { PreparedRequest } from "../protocol/preparedRequest";
import type { Notice } from "../api/notices";
import type { Review, CommandDraft, CaseReceipt } from "../api/service";
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
  mutation: MutationLifecycle<"case.execute">;
  delivery: DeliveryState;
  unresolved: boolean;
  disclosureRefused: boolean;
  operationId: string;
  caseReference: string;
  command: CommandKind;
  values: DraftValues;
  requestBytes: string | null;
  prepared: PreparedRequest<"case.execute"> | null;
  review: Review | null;
  receipt: CaseReceipt | null;
  message: Notice | null;
  fieldError: { name: string; message: Notice } | null;
  exposedRequest: CommandDraft | null;
  pending: { kind: "PREVIEW" | "SUBMIT"; requestId: number } | null;
};
export type OperationAction =
  | { type: "INPUT_INVALID"; field: string; message: Notice }
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
  | {
      type: "PREVIEWING";
      requestId: number;
      draft: CommandDraft;
      prepared?: PreparedRequest<"case.execute">;
    }
  | { type: "REVIEWED"; requestId: number; review: Review }
  | {
      type: "DEFINITELY_REJECTED";
      requestId: number;
      message: Notice;
      field: string | null;
      evidence?: ResolutionEvidence;
      disclosureRefused?: boolean;
    }
  | { type: "SUBMITTING"; requestId: number }
  | { type: "ACCEPTED"; requestId: number; receipt: CaseReceipt; message?: Notice | null }
  | { type: "OUTCOME_UNKNOWN"; requestId: number; message: Notice }
  | { type: "RETURN_TO_EDIT" }
  | { type: "RESET_MESSAGE" };

type DerivedDelivery =
  "delivery" | "prepared" | "requestBytes" | "exposedRequest" | "unresolved" | "pending";
export const projectOperation = (state: Omit<OperationState, DerivedDelivery>): OperationState => {
  const { mutation } = state;
  const { prepared } = mutation;
  return {
    ...state,
    delivery: mutation.phase,
    prepared,
    requestBytes: prepared?.bytes ?? null,
    exposedRequest: prepared?.input ?? null,
    unresolved:
      mutation.phase === "OUTCOME_UNKNOWN" ||
      (mutation.phase === "SUBMITTING" && mutation.prior === "UNCONFIRMED"),
    pending:
      mutation.phase === "SUBMITTING" || mutation.phase === "PREVIEWING"
        ? {
            kind: mutation.phase === "PREVIEWING" ? "PREVIEW" : "SUBMIT",
            requestId: mutation.requestId,
          }
        : null,
  };
};
