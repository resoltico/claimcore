/* Generated from ClaimCore.Contracts. Do not edit. */
import type { Rejection } from "./web-v2.types.diagnostics";
import type { Fault, Receipt } from "./web-v2.types.core";
import type {
  AdvisoryReview,
  DefiniteExecution,
  PreparationDetails,
  PreparationSummary,
  RecoveryRejection,
} from "./web-v2.types.recovery";

export type WebV2CommandResponseByEndpoint = {
  readonly "command.prepare": {
    readonly endpoint: "command.prepare";
    readonly outcome:
      | {
          readonly tag: "PREPARED";
          readonly data: { readonly details: PreparationDetails; readonly review: AdvisoryReview };
        }
      | { readonly tag: "OBSERVED_ACCEPTED"; readonly data: { readonly receipt: Receipt } }
      | {
          readonly tag: "RETAINED_FOR_RECOVERY";
          readonly data: { readonly details: PreparationDetails; readonly rejection: Rejection };
        }
      | {
          readonly tag: "REJECTED";
          readonly data: { readonly operationId: string; readonly rejection: Rejection };
        }
      | {
          readonly tag: "FAILED";
          readonly data: { readonly operationId: string; readonly fault: Fault };
        }
      | {
          readonly tag: "CANCELLED_BEFORE_ADMISSION";
          readonly data: { readonly operationId: string };
        }
      | {
          readonly tag: "PREPARATION_STATE_UNKNOWN";
          readonly data: {
            readonly operationId: string;
            readonly requestSha256: string;
            readonly fault: Fault;
          };
        };
  };
  readonly "command.execute": {
    readonly endpoint: "command.execute";
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
};
