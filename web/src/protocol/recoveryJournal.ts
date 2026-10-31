const isObject = (value: unknown): value is Record<string, unknown> =>
  typeof value === "object" && value !== null && !Array.isArray(value);
export type RecoveryScope = {
  readonly serviceOrigin: string;
  readonly installationId: string;
  readonly actorId: string;
};
export type RecoveryRecord = RecoveryScope & {
  readonly format: 1;
  readonly attemptId: string;
  readonly operationId: string;
  readonly subject: "CASE" | "AUTHORITY";
  readonly act: string;
};
export type RecoveryJournal = {
  readonly put: (record: RecoveryRecord) => Promise<void>;
  readonly remove: (record: RecoveryRecord) => Promise<void>;
  readonly list: (scope: RecoveryScope) => Promise<ReadonlyArray<RecoveryRecord>>;
};
const uuid = (value: unknown): value is string =>
  typeof value === "string" &&
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/u.test(value) &&
  value !== "00000000-0000-0000-0000-000000000000";
const isScope = (value: Record<string, unknown>): boolean =>
  typeof value["serviceOrigin"] === "string" &&
  /^[!-~]{1,2048}$/u.test(value["serviceOrigin"]) &&
  uuid(value["installationId"]) &&
  uuid(value["actorId"]);
const isAttempt = (value: Record<string, unknown>): boolean =>
  uuid(value["attemptId"]) &&
  uuid(value["operationId"]) &&
  (value["subject"] === "CASE" || value["subject"] === "AUTHORITY") &&
  typeof value["act"] === "string" &&
  /^[A-Z][A-Z_]{0,39}$/u.test(value["act"]);
export const isRecoveryRecord = (value: unknown): value is RecoveryRecord =>
  isObject(value) &&
  Object.keys(value).length === 8 &&
  value["format"] === 1 &&
  isScope(value) &&
  isAttempt(value);
export const sameRecoveryScope = (left: RecoveryScope, right: RecoveryScope): boolean =>
  left.serviceOrigin === right.serviceOrigin &&
  left.installationId === right.installationId &&
  left.actorId === right.actorId;
export type PreparedDispatch<T> =
  | { readonly kind: "sent"; readonly value: T }
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
  try {
    if (!isRecoveryRecord(record)) {
      return { kind: "notSent", reason: "storage" };
    }
    await journal.put(record);
  } catch {
    return { kind: "notSent", reason: "storage" };
  }
  if (!current()) {
    // A changed actor/installation cannot clear the earlier scope's conservative record.
    return { kind: "notSent", reason: "stale" };
  }
  return { kind: "sent", value: await send() };
};
