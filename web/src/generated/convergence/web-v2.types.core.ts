/* Generated from ClaimCore.Contracts. Do not edit. */
import type { SemanticDefinition } from "./web-v2.types.semantic";

export type HostFailure = {
  readonly kind: "HOST_FAILURE";
  readonly code: string;
  readonly message: string;
  readonly executionPhase: "NOT_STARTED" | "STARTED_UNCONFIRMED" | null;
};
export type Rejection = {
  readonly code:
    | "INVALID_INPUT"
    | "CASE_NOT_FOUND"
    | "CASE_ALREADY_EXISTS"
    | "VERSION_CONFLICT"
    | "CASE_CLOSED"
    | "AMENDMENT_REQUIRES_UNDECIDED"
    | "DECISION_REQUIRED"
    | "PAYMENT_ALREADY_RECORDED"
    | "PAYMENT_NOT_RECORDED"
    | "DECISION_ALREADY_PAID"
    | "ALREADY_CLOSED"
    | "ALREADY_OPENED"
    | "ZERO_DECISION_CANNOT_BE_PAID"
    | "IDEMPOTENCY_CONFLICT";
  readonly message: string;
  readonly field: string | null;
  readonly actualRevision: string | null;
  readonly recommendedAction:
    | "CORRECT_INPUT"
    | "READ_CURRENT"
    | "RETRY_SAFE"
    | "RECOVER_EXACT"
    | "REAUTHENTICATE"
    | "STOP_AND_INVESTIGATE"
    | "NONE_REQUIRED";
};
export type Fault = {
  readonly code:
    | "STORE_UNAVAILABLE"
    | "STORE_INTEGRITY_ERROR"
    | "SCHEMA_MISMATCH"
    | "RECOVERY_CAPACITY_EXCEEDED"
    | "RECOVERY_INTEGRITY_ERROR"
    | "COMMIT_OUTCOME_UNKNOWN"
    | "TECHNICAL_MUTATION_UNKNOWN";
  readonly message: string;
  readonly recommendedAction:
    | "CORRECT_INPUT"
    | "READ_CURRENT"
    | "RETRY_SAFE"
    | "RECOVER_EXACT"
    | "REAUTHENTICATE"
    | "STOP_AND_INVESTIGATE"
    | "NONE_REQUIRED";
};
export type SessionSnapshot = {
  readonly authenticated: boolean;
  readonly antiforgeryToken: string | null;
};
export type DefinitionPayload = {
  readonly semanticFingerprint: string;
  readonly webFingerprint: string;
  readonly runtime: RuntimeContext;
  readonly definition: SemanticDefinition;
};
export type CaseFields = {
  readonly incidentDate: string;
  readonly incidentNotificationDate: string;
  readonly incidentCountry: string;
  readonly claimantName: string;
  readonly insurerName: string;
  readonly claimedAmount: string;
  readonly claimedCurrency: string;
  readonly caseReference: string;
  readonly paymentDecisionDate: string | null;
  readonly payableAmount: string | null;
  readonly payableCurrency: string | null;
  readonly paymentDate: string | null;
  readonly status: "OPENED" | "CLOSED";
} & Readonly<Record<string, string | null>>;
export type CaseView = { readonly fields: CaseFields; readonly revision: string };
export type CurrentCase = {
  readonly case: CaseView;
  readonly availableCommands: ReadonlyArray<
    | "OPEN"
    | "AMEND_REGISTRATION"
    | "DECIDE"
    | "WITHDRAW_DECISION"
    | "RECORD_PAYMENT"
    | "CLEAR_PAYMENT"
    | "CLOSE"
    | "REOPEN"
  >;
};
export type CaseSummary = {
  readonly caseReference: string;
  readonly revision: string;
  readonly status: "OPENED" | "CLOSED";
};
export type Receipt = {
  readonly operationId: string;
  readonly snapshot: CaseView;
  readonly recordedAt: string;
  readonly recordedBy: string;
  readonly replayed: boolean;
  readonly command:
    | "OPEN"
    | "AMEND_REGISTRATION"
    | "DECIDE"
    | "WITHDRAW_DECISION"
    | "RECORD_PAYMENT"
    | "CLEAR_PAYMENT"
    | "CLOSE"
    | "REOPEN";
};
type ChangeSummary = {
  readonly operationId: string;
  readonly revision: string;
  readonly command:
    | "OPEN"
    | "AMEND_REGISTRATION"
    | "DECIDE"
    | "WITHDRAW_DECISION"
    | "RECORD_PAYMENT"
    | "CLEAR_PAYMENT"
    | "CLOSE"
    | "REOPEN";
  readonly recordedAt: string;
  readonly recordedBy: string;
};
export type HistoryEntry =
  | { readonly tag: "SUMMARY"; readonly change: ChangeSummary }
  | { readonly tag: "FULL"; readonly receipt: Receipt };
export type RuntimeContext = {
  readonly productVersion: string;
  readonly effectiveBusinessDate: string;
  readonly timeZoneId: string;
};
export type FieldDiff = {
  readonly fieldName: string;
  readonly before: string | null;
  readonly after: string | null;
};
export type CommandDraft = {
  readonly operationId: string;
  readonly caseReference: string;
  readonly expectedRevision: string;
  readonly command: {
    readonly kind:
      | "OPEN"
      | "AMEND_REGISTRATION"
      | "DECIDE"
      | "WITHDRAW_DECISION"
      | "RECORD_PAYMENT"
      | "CLEAR_PAYMENT"
      | "CLOSE"
      | "REOPEN";
    readonly values: Readonly<Record<string, string>>;
  };
};
