import type { OwnerScope } from "../protocol/ownerScope";
import type { DateEntryPart, DateEntryDrafts } from "./dateEntryDraft";
import {
  type CaseGetRequest,
  type HumanReview,
  type HumanRejection,
  type OperationSubmission,
  type HumanRejection_CONFLICT,
  type HumanReadDocument_case_get,
  type OperationBinding,
} from "@contracts/client";
import type { OperationReceipt, CurrentCase } from "../protocol/types";
import type { Notice } from "../protocol/notices";
import type { OperationSelection } from "../protocol/operationCatalog";
import type { FormValues, FormValue } from "../protocol/forms";
import type { PreparedRequest } from "../protocol/preparedRequest";
import type { TaskSubmission } from "./taskForm";

import type { FieldErrors } from "./inputErrors";
export type EffectIdentity = {
  owner: OwnerScope;
  contextGeneration: number;
  id: string;
  credential: string;
  target: string;
};
export type LocalSource = {
  prepared: PreparedRequest<"operation.execute">;
  submittedRows: TaskSubmission["submittedRows"];
};
export type ConflictSource = LocalSource & {
  prepared: PreparedRequest<"operation.execute"> & {
    input: Exclude<OperationSubmission, { binding: { kind: "PEOPLE" } }>;
  };
};
export type ConflictObservation = {
  readonly current: HumanReadDocument_case_get;
  readonly revision: number | null;
};
export type RevisionConflictSnapshot = {
  readonly identity: EffectIdentity;
  readonly source: ConflictSource;
  readonly minimumRevision: number;
  readonly diagnostic: HumanRejection_CONFLICT | null;
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
      reviewIdentity: EffectIdentity;
      confirmed: boolean;
    }
  | {
      kind: "PREPARING" | "SUBMITTING";
      source: LocalSource;
      effect: EffectIdentity;
      reviewIdentity: EffectIdentity | null;
    }
  | {
      kind: "UNRESOLVED";
      source: LocalSource;
      pending: { readonly kind: "EXECUTE" | "LOOKUP"; readonly identity: EffectIdentity } | null;
    }
  | {
      kind: "ACCEPTED";
      source: LocalSource;
    };
export type OperationEffect =
  | {
      kind: "READ_CONFLICT";
      identity: EffectIdentity;
      conflictId: string;
      source: ConflictSource;
      minimumRevision: number;
    }
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
    }
  | {
      kind: "DISCLOSURE_REFUSED";
      identity: EffectIdentity;
      notice: Notice;
    };
type OperationContext = Readonly<{
  generation: number;
  owner: OwnerScope | null;
  epoch: number;
  credential: string;
  target: string;
}>;
export type OperationDraft = {
  owner: OwnerScope | null;
  entry: OperationSelection;
  target: string;
  binding: OperationBinding;
  values: FormValues;
  dateEntries: DateEntryDrafts;
  baseline: FormValues;
  generation: number;
  pendingDiscard: string | null;
};
export type OperationDelivery = {
  owner: OwnerScope | null;
  credential: string;
  phase: OperationPhase;
  notice: Extract<Notice, { kind: "local" | "pending" | "accepted" }> | null;
};
type DisclosureRead =
  | { resource: "case.get"; identity: EffectIdentity; query: CaseGetRequest }
  | { resource: "conflict"; identity: EffectIdentity; query: ConflictSource };
export type DisclosedResource = {
  review: { identity: EffectIdentity; value: HumanReview } | null;
  receipt: OperationReceipt | null;
  context: OperationContext;
  confirmedContext: OperationContext;
  caseObservation: CurrentCase | null;
  read: DisclosureRead | null;
  conflict: RevisionConflictSnapshot | null;
  refused: boolean;
  revision: number;
  notice: Notice | null;
  fieldErrors: FieldErrors;
  rejection: HumanRejection | null;
};
/** One coordinator commits cross-lifetime transitions and its ephemeral effect outbox. */
export type OperationState = {
  draft: OperationDraft;
  delivery: OperationDelivery;
  disclosed: DisclosedResource;
  outbox: ReadonlyArray<OperationEffect>;
};
export type OperationAction =
  | {
      type: "DATE_ENTRY";
      path: string;
      canonical: string;
      parts: ReadonlyArray<DateEntryPart> | null;
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
      source: LocalSource;
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
    }
  | {
      type: "EDIT_FIELDS";
      values: FormValues;
      omitted: ReadonlyArray<string>;
    }
  | {
      type: "DISCARD";
      kind: string | null;
    }
  | {
      type: "PREVIEW";
      source: LocalSource;
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
      owner: OwnerScope | null;
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
    }
  | {
      type: "EFFECT_STARTED";
      id: string;
    };
export const operationSource = (phase: OperationPhase): LocalSource | null =>
  phase.kind === "EDITING" ? phase.exposed : phase.source;
