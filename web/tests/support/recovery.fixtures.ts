import { inventory } from "./inventory";
import {
  sameRecoveryScope,
  type RecoveryJournal,
  type RecoveryRecord,
  type RecoveryScope,
} from "../../src/protocol/recoveryJournal";
export const recoveryScope: RecoveryScope = {
  serviceOrigin: typeof location === "undefined" ? "https://service.example" : location.origin,
  installationId: "90000000-0000-4000-8000-000000000001",
  actorId: inventory.cliSamples.actorId,
};
const records = new Map<string, RecoveryRecord>();
const key = (record: RecoveryRecord) =>
  JSON.stringify([record.serviceOrigin, record.installationId, record.actorId, record.attemptId]);
const changed = () => globalThis.dispatchEvent(new Event("claimcore-recovery-changed"));
/** UI test port only; IndexedDB qualification uses the real adapter separately. */
export const testJournal: RecoveryJournal = {
  put: (record) => {
    records.set(key(record), record);
    changed();
    return Promise.resolve();
  },
  remove: (record) => {
    records.delete(key(record));
    changed();
    return Promise.resolve();
  },
  list: (scope) =>
    Promise.resolve([...records.values()].filter((record) => sameRecoveryScope(scope, record))),
};
export const clearRecoveryFixtures = () => {
  records.clear();
};
export const recoveryOptions = { recoveryScope, journal: testJournal };
export const recoveryRecord = (
  attemptId = "10000000-0000-4000-8000-000000000002",
): RecoveryRecord => ({
  ...recoveryScope,
  format: 1,
  attemptId,
  operationId: "10000000-0000-4000-8000-000000000001",
  subject: "CASE",
  act: "CLOSE",
});
