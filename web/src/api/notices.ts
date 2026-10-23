import type { Diagnostic, Fault, HostFailure } from "@contracts/client";
export type DiagnosticNotice = {
  readonly code: Diagnostic["code"] | Fault["code"] | HostFailure["diagnostic"]["id"];
  readonly limits: Readonly<Record<string, number | string>>;
};
export type LocalNoticeReason =
  | "responseTimeout"
  | "unreachable"
  | "incomplete"
  | "invalidInput"
  | "cancelled"
  | "definitionMismatch"
  | "invalidSession"
  | "missingCsrf"
  | "outcomeUnknown";
export type Notice =
  | { readonly kind: "local"; readonly reason: LocalNoticeReason }
  | { readonly kind: "diagnostic"; readonly diagnostic: DiagnosticNotice }
  | { readonly kind: "invalidHttp"; readonly status: number }
  | { readonly kind: "accepted"; readonly operationId: string };
export const localNotice = (reason: LocalNoticeReason): Notice => ({ kind: "local", reason });
