import type { Fault, HostFailure, Rejection } from "@contracts/types";
export type Diagnostic = Fault["diagnostic"] | Rejection["diagnostic"] | HostFailure["diagnostic"];
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
  | { readonly kind: "diagnostic"; readonly diagnostic: Diagnostic }
  | { readonly kind: "invalidHttp"; readonly status: number }
  | { readonly kind: "accepted"; readonly operationId: string };
export const localNotice = (reason: LocalNoticeReason): Notice => ({ kind: "local", reason });
