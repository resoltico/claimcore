import { isCanonicalUuid } from "./uuid";
import type { PrincipalScope } from "./ownerScope";
import { taskDefinitions } from "@contracts/client";
const isObject = (value: unknown): value is Record<string, unknown> =>
  typeof value === "object" && value !== null && !Array.isArray(value);
export type RecoveryScope = PrincipalScope;
export const recoveryRecordFormat = 2;
export type RecoveryRecord = RecoveryScope & {
  readonly format: typeof recoveryRecordFormat;
  readonly attemptId: string;
  readonly operationId: string;
  readonly subject:
    "CASE" | "PEOPLE" | "WORK" | "REMINDER" | "NOTE" | "PARTY" | "LINK" | "ATTACHMENT";
  readonly act: string;
};
export type JournalPublication = "inserted" | "existing";
export type RecoveryJournal = {
  readonly put: (record: RecoveryRecord) => Promise<JournalPublication>;
  readonly remove: (record: RecoveryRecord) => Promise<void>;
  readonly confirm?: (record: RecoveryRecord) => Promise<void>;
  readonly confirmed?: (scope: RecoveryScope) => Promise<ReadonlyArray<RecoveryRecord>>;
  readonly list: (scope: RecoveryScope) => Promise<ReadonlyArray<RecoveryRecord>>;
};
const isScope = (value: Record<string, unknown>): boolean =>
  typeof value["serviceOrigin"] === "string" &&
  /^[!-~]{1,2048}$/u.test(value["serviceOrigin"]) &&
  isCanonicalUuid(value["installationId"]) &&
  isCanonicalUuid(value["actorId"]);
const isAttempt = (value: Record<string, unknown>): boolean =>
  isCanonicalUuid(value["attemptId"]) &&
  isCanonicalUuid(value["operationId"]) &&
  ["CASE", "PEOPLE", "WORK", "REMINDER", "NOTE", "PARTY", "LINK", "ATTACHMENT"].includes(
    String(value["subject"]),
  ) &&
  typeof value["act"] === "string" &&
  taskDefinitions.some(
    (task) =>
      task.kind === "OPERATION" && task.subject === value["subject"] && task.id === value["act"],
  );

export const isRecoveryRecord = (value: unknown): value is RecoveryRecord =>
  isObject(value) &&
  Object.keys(value).length === 8 &&
  value["format"] === recoveryRecordFormat &&
  isScope(value) &&
  isAttempt(value);
export const sameRecoveryScope = (left: RecoveryScope, right: RecoveryScope): boolean =>
  left.serviceOrigin === right.serviceOrigin &&
  left.installationId === right.installationId &&
  left.actorId === right.actorId;
export type PreparedDispatch<T> =
  | { readonly kind: "sent"; readonly publication: JournalPublication; readonly value: T }
  | { readonly kind: "notSent"; readonly reason: "storage" | "stale" };
/** The journal port resolves only after durable publication/transaction completion. */
export const journalBeforeSend = async <T>(
  journal: RecoveryJournal,
  record: RecoveryRecord,
  current: () => boolean,
  send: () => Promise<T>,
): Promise<PreparedDispatch<T>> => {
  if (!current()) {
    return { kind: "notSent", reason: "stale" };
  }
  let publication: JournalPublication;
  try {
    if (!isRecoveryRecord(record)) {
      return { kind: "notSent", reason: "storage" };
    }
    publication = await journal.put(record);
  } catch {
    return { kind: "notSent", reason: "storage" };
  }
  if (!current()) {
    // A changed actor/installation cannot clear the earlier scope's conservative record.
    return { kind: "notSent", reason: "stale" };
  }
  return { kind: "sent", publication, value: await send() };
};
