import type { DateEntryPart, DateEntryDrafts } from "./dateEntryDraft";
import type {
  HumanReview,
  HumanRejection,
  OperationInput,
  Diagnostic_REVISION_CONFLICT,
  CaseView,
  Screen,
} from "@contracts/client";
import type { OperationReceipt, CurrentCase } from "../protocol/types";
import type { Notice } from "../protocol/notices";
import type { CatalogEntryIdentity } from "../protocol/catalogAssembly";
import type { FormValues, FormValue } from "../protocol/forms";
import type { PreparedRequest } from "../protocol/preparedRequest";
import type { TaskSubmission } from "./taskForm";
import type { FieldErrors } from "./inputErrors";
export type EffectIdentity = {
  contextGeneration: number;
  id: string;
  credential: string;
  target: string;
};
export type LocalSource = {
  prepared: PreparedRequest<"operation.execute">;
  submittedRows?: TaskSubmission["submittedRows"];
};
export type ConflictSource = LocalSource & {
  prepared: PreparedRequest<"operation.execute"> & {
    input: Exclude<OperationInput, { kind: "AUTHORITY" }>;
  };
};
export type ConflictObservation = {
  readonly current: CaseView & { readonly view: Screen };
  readonly revision: number | null;
};
export type RevisionConflictSnapshot = {
  readonly identity: EffectIdentity;
  readonly source: ConflictSource;
  readonly diagnostic: Diagnostic_REVISION_CONFLICT;
  readonly latest: ConflictObservation | null;
};
export type OperationPhase =
  | {
      kind: "EDITING";
      exposed: LocalSource | null;
    }
  | {
      kind: "PREVIEWING";
      source: LocalSource;
      effect: EffectIdentity;
    }
  | {
      kind: "REVIEWING";
      source: LocalSource;
      review: HumanReview;
      confirmed: boolean;
    }
  | {
      kind: "PREPARING" | "SUBMITTING";
      source: LocalSource;
      effect: EffectIdentity;
      review: HumanReview | null;
    }
  | {
      kind: "UNRESOLVED";
      source: LocalSource;
      effect: EffectIdentity | null;
    }
  | {
      kind: "ACCEPTED";
      source: LocalSource;
      receipt: OperationReceipt | null;
    };
export type OperationEffect =
  | { kind: "READ_CONFLICT"; identity: EffectIdentity; snapshot: RevisionConflictSnapshot }
  | { kind: "READ_CURRENT"; identity: EffectIdentity; caseId: string; reference: string }
  | {
      kind: "PREVIEW";
      identity: EffectIdentity;
      source: LocalSource;
    }
  | {
      kind: "EXECUTE";
      identity: EffectIdentity;
      source: LocalSource;
    }
  | {
      kind: "CHECK_OPERATION";
      identity: EffectIdentity;
      source: LocalSource;
    }
  | {
      kind: "RECORDED";
      identity: EffectIdentity;
      receipt: OperationReceipt;
    }
  | {
      kind: "DISCLOSURE_REFUSED";
      identity: EffectIdentity;
      notice: Notice;
    };
type OperationContext = Readonly<{
  generation: number;
  epoch: number;
  credential: string;
  target: string;
}>;
export type OperationState = {
  entry: CatalogEntryIdentity;
  target: string;
  context: OperationContext;
  disclosureContext: OperationContext;
  caseObservation: CurrentCase | null;
  disclosureRead: EffectIdentity | null;
  conflict: RevisionConflictSnapshot | null;
  disclosureRefused: boolean;
  /** Ordering fence for ephemeral successful-context observations; never serialized. */
  disclosureRevision: number;
  values: FormValues;
  dateEntries: DateEntryDrafts;
  baseline: FormValues;
  generation: number;
  operation: OperationPhase;
  effects: ReadonlyArray<OperationEffect>;
  notice: Notice | null;
  fieldErrors: FieldErrors;
  rejection: HumanRejection | null;
  pendingDiscard: string | null;
};
export type OperationAction =
  | {
      type: "DATE_ENTRY";
      path: string;
      canonical: string;
      parts: ReadonlyArray<DateEntryPart> | null;
      id: string;
    }
  | { type: "CLEAR_DATE_ENTRIES" }
  | {
      type: "RESTORE_CREATION_DRAFT";
      identity: EffectIdentity;
      editing: OperationPhase;
      disclosureRevision: number;
    }
  | { type: "READ_CONFLICT"; identity: EffectIdentity; conflictId: string }
  | {
      type: "CONFLICT_OBSERVED";
      identity: EffectIdentity;
      conflictId: string;
      value: ConflictObservation;
    }
  | {
      type: "CONFLICT_READ_FAILED";
      identity: EffectIdentity;
      conflictId: string;
      notice: Notice;
      refused: boolean;
    }
  | {
      type: "REVIEW_LATEST";
      identity: EffectIdentity;
      conflictId: string;
      generation: number;
      values: FormValues;
      prepared: PreparedRequest<"operation.execute">;
    }
  | { type: "READ_CURRENT"; identity: EffectIdentity; caseId: string; reference: string }
  | { type: "DISCLOSED"; identity: EffectIdentity; value: CurrentCase }
  | { type: "DISCLOSURE_READ_FAILED"; identity: EffectIdentity; notice: Notice; refused: boolean }
  | { type: "HANDOFF"; identity: EffectIdentity }
  | {
      type: "RESET";
      state: OperationState;
      abandon: boolean;
    }
  | {
      type: "EDIT";
      path: string;
      value: FormValue;
      id: string;
    }
  | {
      type: "EDIT_FIELDS";
      values: FormValues;
      omitted: ReadonlyArray<string>;
      id: string;
    }
  | {
      type: "DISCARD";
      kind: string | null;
    }
  | {
      type: "PREVIEW";
      prepared: PreparedRequest<"operation.execute">;
      identity: EffectIdentity;
      generation: number;
    }
  | {
      type: "REVIEW";
      review: HumanReview;
      identity: EffectIdentity;
    }
  | {
      type: "RECORD";
      receipt: OperationReceipt;
      identity: EffectIdentity;
    }
  | {
      type: "REFUSE";
      rejection?: HumanRejection | undefined;
      notice: Notice;
      target: string | null;
      uncertain: boolean;
      disclosure: boolean;
      identity?: EffectIdentity;
    }
  | {
      type: "CONTEXT";
      epoch: number;
      credential: string;
      target: string;
    }
  | {
      type: "INVALIDATE";
      refused: boolean;
    }
  | {
      type: "RESTORE";
    }
  | {
      type: "RETURN";
    }
  | {
      type: "CONFIRM";
      value: boolean;
    }
  | {
      type: "RENEW_PROVISIONAL";
      identity: EffectIdentity;
      prepared: PreparedRequest<"operation.execute">;
    }
  | {
      type: "EXECUTE";
      identity: EffectIdentity;
    }
  | {
      type: "CHECK_OPERATION";
      identity: EffectIdentity;
    }
  | {
      type: "OPERATION_PENDING";
      identity: EffectIdentity;
      finalAt: string;
    }
  | {
      type: "NOT_ACCEPTED";
      identity: EffectIdentity;
      id: string;
    }
  | {
      type: "EFFECT_STARTED";
      id: string;
    };
export const operationSource = (phase: OperationPhase): LocalSource | null =>
  phase.kind === "EDITING" ? phase.exposed : phase.source;
