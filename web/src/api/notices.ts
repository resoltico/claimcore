import type {
  Fault,
  HostFailure,
  RecoveryRejection,
  Rejection,
} from "../generated/convergence/web-v2.types";

/** Inert presentation data, never an outcome, retry decision or locale preference. */
export type Diagnostic =
  | Fault["diagnostic"]
  | Rejection["diagnostic"]
  | RecoveryRejection["diagnostic"]
  | HostFailure["diagnostic"];
export type LocalNoticeReason =
  | "unreachable"
  | "fileUnreadable"
  | "exportInvalid"
  | "incomplete"
  | "definitionMismatch"
  | "invalidSession"
  | "missingCsrf"
  | "dismissed"
  | "alreadyDismissed"
  | "retained"
  | "existing"
  | "digestUnavailable"
  | "exportStarted"
  | "keptForRecovery";
export type Notice =
  | { readonly kind: "local"; readonly reason: LocalNoticeReason }
  | { readonly kind: "diagnostic"; readonly diagnostic: Diagnostic }
  | { readonly kind: "invalidHttp"; readonly status: number }
  | { readonly kind: "fileTooLarge"; readonly maximumBytes: number }
  | { readonly kind: "accepted"; readonly operationId: string }
  | {
      readonly kind: "recovery";
      readonly cause: Notice;
      readonly direction: "inspectBeforeRetry" | "inspectBeforeAction";
    };
export const localNotice = (reason: LocalNoticeReason): Notice => ({ kind: "local", reason });
export const recoveryNotice = (
  cause: Notice,
  direction: "inspectBeforeRetry" | "inspectBeforeAction",
): Notice => ({ kind: "recovery", cause, direction });
