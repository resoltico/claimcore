/* Generated from ClaimCore.Contracts. Do not edit. */
import type { SemanticDefinition } from "./web-v2.types.semantic";

export type HostFailure = {
  readonly kind: "HOST_FAILURE";
  readonly code: string;
  readonly message: string;
  readonly executionPhase: "NOT_STARTED" | "STARTED_UNCONFIRMED" | null;
};
export type Fault =
  | {
      readonly code: "TECHNICAL_MUTATION_UNKNOWN";
      readonly diagnostic: {
        readonly id: "CORE_OPERATION_CONTENT_CONFLICT" | "RECOVERY_STORE_CONTENT_CONFLICT";
        readonly parameters: Readonly<Record<string, never>>;
      };
      readonly message: string;
      readonly recommendedAction: "STOP_AND_INVESTIGATE";
    }
  | {
      readonly code: "STORE_UNAVAILABLE";
      readonly diagnostic: {
        readonly id:
          | "CORE_STORE_UNAVAILABLE"
          | "RECOVERY_STORE_UNAVAILABLE"
          | "RECOVERY_READ_CANCELLED"
          | "RECOVERY_MUTATION_CANCELLED_BEFORE_COMMIT";
        readonly parameters: Readonly<Record<string, never>>;
      };
      readonly message: string;
      readonly recommendedAction: "RETRY_SAFE";
    }
  | {
      readonly code: "COMMIT_OUTCOME_UNKNOWN";
      readonly diagnostic: {
        readonly id: "CORE_COMMIT_OUTCOME_UNKNOWN";
        readonly parameters: Readonly<Record<string, never>>;
      };
      readonly message: string;
      readonly recommendedAction: "RECOVER_EXACT";
    }
  | {
      readonly code: "STORE_INTEGRITY_ERROR";
      readonly diagnostic: {
        readonly id: "CORE_STORE_INTEGRITY_ERROR";
        readonly parameters: Readonly<Record<string, never>>;
      };
      readonly message: string;
      readonly recommendedAction: "STOP_AND_INVESTIGATE";
    }
  | {
      readonly code: "SCHEMA_MISMATCH";
      readonly diagnostic: {
        readonly id: "CORE_SCHEMA_MISMATCH" | "RECOVERY_SCHEMA_MISMATCH";
        readonly parameters: Readonly<Record<string, never>>;
      };
      readonly message: string;
      readonly recommendedAction: "STOP_AND_INVESTIGATE";
    }
  | {
      readonly code: "RECOVERY_INTEGRITY_ERROR";
      readonly diagnostic: {
        readonly id:
          | "RECOVERY_STORE_RESPONSE_INVALID"
          | "RECOVERY_STORE_PREPARATION_MISSING"
          | "RECOVERY_STORE_INTEGRITY_ERROR"
          | "RECOVERY_RETAINED_CANONICAL_INVALID"
          | "RECOVERY_RETAINED_DOMAIN_SHAPE_INVALID"
          | "RECOVERY_RETAINED_PREPARATION_UNVERIFIABLE"
          | "RECOVERY_NEW_PREPARATION_MISSING"
          | "RECOVERY_RETAINED_DIGEST_MISMATCH"
          | "RECOVERY_STORED_DATA_INVALID";
        readonly parameters: Readonly<Record<string, never>>;
      };
      readonly message: string;
      readonly recommendedAction: "STOP_AND_INVESTIGATE";
    }
  | {
      readonly code: "RECOVERY_CAPACITY_EXCEEDED";
      readonly diagnostic: {
        readonly id: "RECOVERY_CAPACITY_EXHAUSTED";
        readonly parameters: Readonly<Record<string, never>>;
      };
      readonly message: string;
      readonly recommendedAction: "STOP_AND_INVESTIGATE";
    }
  | {
      readonly code: "TECHNICAL_MUTATION_UNKNOWN";
      readonly diagnostic: {
        readonly id:
          "RECOVERY_MUTATION_OUTCOME_UNKNOWN" | "RECOVERY_PREPARATION_DISMISSED_BEFORE_EXECUTION";
        readonly parameters: Readonly<Record<string, never>>;
      };
      readonly message: string;
      readonly recommendedAction: "RECOVER_EXACT";
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
    | "CORRECT_CASE"
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
    | "CORRECT_CASE"
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
    | "CORRECT_CASE"
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
  readonly command:
    | {
        readonly kind: "OPEN";
        readonly values: {
          readonly incidentDate: string;
          readonly incidentNotificationDate: string;
          readonly incidentCountry: string;
          readonly claimantName: string;
          readonly insurerName: string;
          readonly claimedAmount: string;
          readonly claimedCurrency: string;
        };
      }
    | {
        readonly kind: "AMEND_REGISTRATION";
        readonly values: {
          readonly incidentDate: string;
          readonly incidentNotificationDate: string;
          readonly incidentCountry: string;
          readonly claimantName: string;
          readonly insurerName: string;
          readonly claimedAmount: string;
          readonly claimedCurrency: string;
        };
      }
    | {
        readonly kind: "CORRECT_CASE";
        readonly groups: {
          readonly registration:
            | { readonly mode: "KEEP" }
            | {
                readonly mode: "REPLACE";
                readonly values: {
                  readonly incidentDate: string;
                  readonly incidentNotificationDate: string;
                  readonly incidentCountry: string;
                  readonly claimantName: string;
                  readonly insurerName: string;
                  readonly claimedAmount: string;
                  readonly claimedCurrency: string;
                };
              };
          readonly decision:
            | { readonly mode: "KEEP" }
            | {
                readonly mode: "REPLACE";
                readonly values: {
                  readonly paymentDecisionDate: string;
                  readonly payableAmount: string;
                  readonly payableCurrency: string;
                };
              }
            | { readonly mode: "CLEAR" };
          readonly payment:
            | { readonly mode: "KEEP" }
            | { readonly mode: "REPLACE"; readonly values: { readonly paymentDate: string } }
            | { readonly mode: "CLEAR" };
        };
      }
    | {
        readonly kind: "DECIDE";
        readonly values: {
          readonly paymentDecisionDate: string;
          readonly payableAmount: string;
          readonly payableCurrency: string;
        };
      }
    | { readonly kind: "WITHDRAW_DECISION"; readonly values: Readonly<Record<string, never>> }
    | { readonly kind: "RECORD_PAYMENT"; readonly values: { readonly paymentDate: string } }
    | { readonly kind: "CLEAR_PAYMENT"; readonly values: Readonly<Record<string, never>> }
    | { readonly kind: "CLOSE"; readonly values: Readonly<Record<string, never>> }
    | { readonly kind: "REOPEN"; readonly values: Readonly<Record<string, never>> };
};
