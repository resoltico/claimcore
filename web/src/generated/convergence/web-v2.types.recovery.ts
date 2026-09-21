/* Generated from ClaimCore.Contracts. Do not edit. */
import type { Rejection } from "./web-v2.types.diagnostics";
import type { CaseView, FieldDiff, Fault, Receipt, RuntimeContext } from "./web-v2.types.core";

export type RecoveryRejection =
  | {
      readonly code: "INVALID_RECOVERY_INPUT";
      readonly diagnostic: {
        readonly id:
          | "RECOVERY_OPERATION_ID_REQUIRED"
          | "RECOVERY_REQUEST_DIGEST_INVALID"
          | "RECOVERY_PAGE_LIMIT_RANGE"
          | "RECOVERY_LIST_CURSOR_INVALID"
          | "RECOVERY_LIST_CURSOR_VIEW_MISMATCH"
          | "RECOVERY_ATTEMPT_CURSOR_INVALID"
          | "RECOVERY_ATTEMPT_CURSOR_OPERATION_MISMATCH"
          | "RECOVERY_DISMISSAL_CONFIRMATION_REQUIRED";
        readonly parameters: Readonly<Record<string, never>>;
      };
      readonly message: string;
      readonly recommendedAction: "CORRECT_INPUT";
    }
  | {
      readonly code: "PREPARATION_NOT_FOUND";
      readonly diagnostic: {
        readonly id: "RECOVERY_PREPARATION_NOT_FOUND";
        readonly parameters: Readonly<Record<string, never>>;
      };
      readonly message: string;
      readonly recommendedAction: "READ_CURRENT";
    }
  | {
      readonly code: "RECOVERY_IDEMPOTENCY_CONFLICT";
      readonly diagnostic: {
        readonly id: "RECOVERY_OPERATION_CONTENT_CONFLICT";
        readonly parameters: Readonly<Record<string, never>>;
      };
      readonly message: string;
      readonly recommendedAction: "STOP_AND_INVESTIGATE";
    }
  | {
      readonly code: "PREPARATION_DISMISSED";
      readonly diagnostic: {
        readonly id: "RECOVERY_PREPARATION_DISMISSED";
        readonly parameters: Readonly<Record<string, never>>;
      };
      readonly message: string;
      readonly recommendedAction: "READ_CURRENT";
    }
  | {
      readonly code: "SUBMISSION_ALREADY_STARTED";
      readonly diagnostic: {
        readonly id: "RECOVERY_SUBMISSION_ALREADY_STARTED";
        readonly parameters: Readonly<Record<string, never>>;
      };
      readonly message: string;
      readonly recommendedAction: "RECOVER_EXACT";
    }
  | {
      readonly code: "RECOVERY_ACTION_UNAVAILABLE";
      readonly diagnostic: {
        readonly id: "RECOVERY_ACCEPTED_OPERATION_DISMISSAL_FORBIDDEN";
        readonly parameters: Readonly<Record<string, never>>;
      };
      readonly message: string;
      readonly recommendedAction: "READ_CURRENT";
    }
  | {
      readonly code: "SOURCE_DIGEST_MISMATCH";
      readonly diagnostic: {
        readonly id: "RECOVERY_SOURCE_DIGEST_MISMATCH" | "RECOVERY_REQUEST_DIGEST_MISMATCH";
        readonly parameters: Readonly<Record<string, never>>;
      };
      readonly message: string;
      readonly recommendedAction: "STOP_AND_INVESTIGATE";
    }
  | {
      readonly code: "INSTALLATION_MISMATCH";
      readonly diagnostic: {
        readonly id: "RECOVERY_INSTALLATION_MISMATCH";
        readonly parameters: Readonly<Record<string, never>>;
      };
      readonly message: string;
      readonly recommendedAction: "STOP_AND_INVESTIGATE";
    }
  | {
      readonly code: "UNSUPPORTED_RECOVERY_ARTIFACT";
      readonly diagnostic: {
        readonly id:
          | "RECOVERY_ENVELOPE_INVALID_OR_UNSUPPORTED"
          | "RECOVERY_CANONICAL_RECORD_INVALID_OR_UNSUPPORTED";
        readonly parameters: Readonly<Record<string, never>>;
      };
      readonly message: string;
      readonly recommendedAction: "CORRECT_INPUT";
    }
  | {
      readonly code: "OPERATION_REVOKED";
      readonly diagnostic: {
        readonly id: "RECOVERY_OPERATION_REVOKED";
        readonly parameters: Readonly<Record<string, never>>;
      };
      readonly message: string;
      readonly recommendedAction: "READ_CURRENT";
    }
  | {
      readonly code: "ATTEMPT_LIMIT_REACHED";
      readonly diagnostic: {
        readonly id: "RECOVERY_ATTEMPT_LIMIT_REACHED";
        readonly parameters: Readonly<Record<string, never>>;
      };
      readonly message: string;
      readonly recommendedAction: "READ_CURRENT";
    };
export type PreparationSummary = {
  readonly operationId: string;
  readonly caseReference: string;
  readonly command:
    | "OPEN"
    | "AMEND_REGISTRATION"
    | "CORRECT_CASE"
    | "DECIDE"
    | "WITHDRAW_DECISION"
    | "RECORD_PAYMENT"
    | "CLEAR_PAYMENT"
    | "CLOSE"
    | "REOPEN";
  readonly preparedAt: string;
  readonly state: "UNSUBMITTED" | "SUBMISSION_STARTED" | "DISMISSED" | "REVOKED";
  readonly authority: "PENDING" | "ACCEPTED" | "REVOKED";
  readonly requestSha256: string | null;
  readonly availableActions: ReadonlyArray<"RESOLVE" | "DISMISS" | "EXPORT">;
};
export type RevokedOperation = {
  readonly operationId: string;
  readonly revokedAt: string;
  readonly reason: string;
};
export type PreparationDetails = {
  readonly summary: PreparationSummary;
  readonly expectedRevision: string;
  readonly authoredValues: ReadonlyArray<{ readonly name: string; readonly value: string }>;
  readonly canonicalCommandFormat: 2;
  readonly preparingApplicationVersion: string;
  readonly preparingContractFingerprint: string;
  readonly preparingContractKind: "LEGACY_UNCLASSIFIED" | "SEMANTIC_CORE_V1";
  readonly attempts: {
    readonly items: ReadonlyArray<{
      readonly attemptId: string;
      readonly startedAt: string;
      readonly settlement: "ACCEPTED" | "REJECTED" | "ERROR" | "REVOKED_BEFORE_EXECUTION" | null;
      readonly settledAt: string | null;
    }>;
    readonly nextCursor: string | null;
    readonly legacyUncertainty: boolean;
  };
};
export type RecoveryListItem =
  | { readonly tag: "RETAINED"; readonly summary: PreparationSummary }
  | { readonly tag: "REVOKED"; readonly revocation: RevokedOperation };
export type RecoveryPage = {
  readonly view: "PENDING" | "TERMINAL";
  readonly items: ReadonlyArray<RecoveryListItem>;
  readonly nextCursor: string | null;
  readonly pendingPreparationCount: number;
  readonly pendingCanonicalRequestBytes: number;
  readonly maximumPendingPreparations: number;
  readonly maximumPendingCanonicalRequestBytes: number;
  readonly nearCapacity: boolean;
};
export type AdvisoryReview = {
  readonly before: CaseView | null;
  readonly proposed: CaseView;
  readonly changes: ReadonlyArray<FieldDiff>;
  readonly context: RuntimeContext;
  readonly advisory: boolean;
};
export type RecoveryImportPreview = {
  readonly artifactKind: "ENVELOPE" | "UNBOUND_CANONICAL_RECORD";
  readonly sourceSha256: string;
  readonly decodedEffect: {
    readonly operationId: string;
    readonly caseReference: string;
    readonly command:
      | "OPEN"
      | "AMEND_REGISTRATION"
      | "CORRECT_CASE"
      | "DECIDE"
      | "WITHDRAW_DECISION"
      | "RECORD_PAYMENT"
      | "CLEAR_PAYMENT"
      | "CLOSE"
      | "REOPEN";
    readonly expectedRevision: string;
    readonly authoredValues: ReadonlyArray<{ readonly name: string; readonly value: string }>;
    readonly canonicalCommandFormat: 2;
    readonly requestSha256: string;
  };
  readonly existingPreparation: PreparationSummary | null;
};
export type RecoveryDetails = {
  readonly preparation: PreparationDetails;
  readonly observation:
    | { readonly tag: "FOUND"; readonly value: Receipt }
    | { readonly tag: "NOT_FOUND"; readonly identity: string };
};
export type RecoveryInspection =
  | { readonly tag: "RETAINED"; readonly value: RecoveryDetails }
  | { readonly tag: "REVOKED"; readonly revocation: RevokedOperation };
export type DefiniteExecution =
  | { readonly tag: "ACCEPTED"; readonly receipt: Receipt }
  | { readonly tag: "REJECTED"; readonly operationId: string; readonly rejection: Rejection }
  | { readonly tag: "REVOKED_BEFORE_EXECUTION"; readonly operationId: string }
  | { readonly tag: "FAILED_BEFORE_COMMIT"; readonly operationId: string; readonly fault: Fault };
