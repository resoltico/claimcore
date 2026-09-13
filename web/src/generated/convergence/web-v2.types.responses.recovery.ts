/* Generated from ClaimCore.Contracts. Do not edit. */
import type { Fault, Receipt } from "./web-v2.types.core";
import type {
  DefiniteExecution,
  PreparationDetails,
  PreparationSummary,
  RecoveryDetails,
  RecoveryImportPreview,
  RecoveryRejection,
} from "./web-v2.types.recovery";

export type WebV2RecoveryResponseByEndpoint = {
  readonly "recovery.list": {
    readonly endpoint: "recovery.list";
    readonly outcome:
      | {
          readonly tag: "SUCCEEDED";
          readonly data: {
            readonly items: ReadonlyArray<PreparationSummary>;
            readonly nextCursor: string | null;
          };
        }
      | { readonly tag: "REJECTED"; readonly data: RecoveryRejection }
      | { readonly tag: "FAILED"; readonly data: Fault }
      | { readonly tag: "CANCELLED"; readonly data: null };
  };
  readonly "recovery.inspect": {
    readonly endpoint: "recovery.inspect";
    readonly outcome:
      | {
          readonly tag: "SUCCEEDED";
          readonly data:
            | { readonly tag: "FOUND"; readonly value: RecoveryDetails }
            | { readonly tag: "NOT_FOUND"; readonly identity: string };
        }
      | { readonly tag: "REJECTED"; readonly data: RecoveryRejection }
      | { readonly tag: "FAILED"; readonly data: Fault }
      | { readonly tag: "CANCELLED"; readonly data: null };
  };
  readonly "recovery.resolve": {
    readonly endpoint: "recovery.resolve";
    readonly outcome:
      | { readonly tag: "OBSERVED_ACCEPTED"; readonly data: { readonly receipt: Receipt } }
      | {
          readonly tag: "COMPLETED";
          readonly data: {
            readonly preparation: PreparationSummary;
            readonly attemptId: string;
            readonly execution: DefiniteExecution;
            readonly settlement: "CONFIRMED" | "UNCONFIRMED";
          };
        }
      | {
          readonly tag: "REFUSED_BEFORE_ATTEMPT";
          readonly data: {
            readonly preparation: PreparationSummary | null;
            readonly rejection: RecoveryRejection;
          };
        }
      | {
          readonly tag: "FAILED_BEFORE_ATTEMPT";
          readonly data: { readonly preparation: PreparationSummary | null; readonly fault: Fault };
        }
      | {
          readonly tag: "CANCELLED_BEFORE_ADMISSION";
          readonly data: { readonly operationId: string };
        }
      | {
          readonly tag: "CANCELLED_BEFORE_ATTEMPT";
          readonly data: { readonly preparation: PreparationSummary };
        }
      | {
          readonly tag: "ATTEMPT_ADMISSION_UNKNOWN";
          readonly data: { readonly preparation: PreparationSummary; readonly fault: Fault };
        }
      | {
          readonly tag: "ATTEMPT_UNRESOLVED";
          readonly data: {
            readonly preparation: PreparationSummary;
            readonly attemptId: string;
            readonly fault: Fault;
          };
        };
  };
  readonly "recovery.dismiss": {
    readonly endpoint: "recovery.dismiss";
    readonly outcome:
      | { readonly tag: "DISMISSED"; readonly data: PreparationDetails }
      | { readonly tag: "ALREADY_DISMISSED"; readonly data: PreparationDetails }
      | { readonly tag: "NOT_FOUND"; readonly data: { readonly operationId: string } }
      | {
          readonly tag: "REFUSED";
          readonly data: {
            readonly details: PreparationDetails | null;
            readonly rejection: RecoveryRejection;
          };
        }
      | { readonly tag: "FAILED"; readonly data: Fault }
      | {
          readonly tag: "CANCELLED_BEFORE_ADMISSION";
          readonly data: { readonly operationId: string };
        }
      | {
          readonly tag: "DISMISS_STATE_UNKNOWN";
          readonly data: {
            readonly operationId: string;
            readonly requestSha256: string;
            readonly fault: Fault;
          };
        };
  };
  readonly "recovery.export": {
    readonly endpoint: "recovery.export";
    readonly outcome:
      | { readonly tag: "NOT_FOUND"; readonly data: { readonly operationId: string } }
      | { readonly tag: "REJECTED"; readonly data: RecoveryRejection }
      | { readonly tag: "FAILED"; readonly data: Fault }
      | { readonly tag: "CANCELLED"; readonly data: null };
  };
  readonly "recovery.importEnvelopePreview": {
    readonly endpoint: "recovery.importEnvelopePreview";
    readonly outcome:
      | { readonly tag: "SUCCEEDED"; readonly data: RecoveryImportPreview }
      | { readonly tag: "REJECTED"; readonly data: RecoveryRejection }
      | { readonly tag: "FAILED"; readonly data: Fault }
      | { readonly tag: "CANCELLED"; readonly data: null };
  };
  readonly "recovery.importEnvelopeRetain": {
    readonly endpoint: "recovery.importEnvelopeRetain";
    readonly outcome:
      | { readonly tag: "RETAINED"; readonly data: PreparationDetails }
      | { readonly tag: "EXISTING"; readonly data: PreparationDetails }
      | { readonly tag: "REJECTED"; readonly data: RecoveryRejection }
      | { readonly tag: "FAILED"; readonly data: Fault }
      | { readonly tag: "CANCELLED_BEFORE_ADMISSION"; readonly data: null }
      | {
          readonly tag: "RETAIN_STATE_UNKNOWN";
          readonly data: {
            readonly artifactKind: "ENVELOPE" | "UNBOUND_CANONICAL_RECORD";
            readonly sourceSha256: string;
            readonly operationId: string | null;
            readonly fault: Fault;
          };
        };
  };
  readonly "recovery.importRecordPreview": {
    readonly endpoint: "recovery.importRecordPreview";
    readonly outcome:
      | { readonly tag: "SUCCEEDED"; readonly data: RecoveryImportPreview }
      | { readonly tag: "REJECTED"; readonly data: RecoveryRejection }
      | { readonly tag: "FAILED"; readonly data: Fault }
      | { readonly tag: "CANCELLED"; readonly data: null };
  };
  readonly "recovery.importRecordRetain": {
    readonly endpoint: "recovery.importRecordRetain";
    readonly outcome:
      | { readonly tag: "RETAINED"; readonly data: PreparationDetails }
      | { readonly tag: "EXISTING"; readonly data: PreparationDetails }
      | { readonly tag: "REJECTED"; readonly data: RecoveryRejection }
      | { readonly tag: "FAILED"; readonly data: Fault }
      | { readonly tag: "CANCELLED_BEFORE_ADMISSION"; readonly data: null }
      | {
          readonly tag: "RETAIN_STATE_UNKNOWN";
          readonly data: {
            readonly artifactKind: "ENVELOPE" | "UNBOUND_CANONICAL_RECORD";
            readonly sourceSha256: string;
            readonly operationId: string | null;
            readonly fault: Fault;
          };
        };
  };
};
