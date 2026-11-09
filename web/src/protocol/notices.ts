import type {
  Diagnostic,
  Fault,
  HostFailure,
  InputIssue,
  HumanAdvice,
  HumanInputIssue,
} from "@contracts/client";
export type DiagnosticNotice = Diagnostic | Fault | HostFailure | InputIssue;
export type LocalNoticeReason =
  | "responseTimeout"
  | "unreachable"
  | "incomplete"
  | "invalidInput"
  | "cancelled"
  | "definitionMismatch"
  | "invalidSession"
  | "missingCsrf"
  | "outcomeUnknown"
  | "notAccepted"
  | "unresolved"
  | "recoveryUnavailable"
  | "recoveryListingUnavailable"
  | "recoveryCleanupUnavailable";
export type Notice =
  | { readonly kind: "human"; readonly advice: HumanAdvice }
  | { readonly kind: "humanIssues"; readonly issues: ReadonlyArray<HumanInputIssue> }
  | { readonly kind: "pending"; readonly finalAt: string }
  | { readonly kind: "issues"; readonly issues: ReadonlyArray<InputIssue> }
  | { readonly kind: "local"; readonly reason: LocalNoticeReason }
  | { readonly kind: "diagnostic"; readonly diagnostic: DiagnosticNotice }
  | { readonly kind: "invalidHttp"; readonly status: number }
  | { readonly kind: "accepted"; readonly operationId: string };
export const localNotice = (reason: LocalNoticeReason): Notice => ({ kind: "local", reason });
