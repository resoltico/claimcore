import { expect, it, vi } from "vitest";
import {
  journalBeforeSend,
  isRecoveryRecord,
  type RecoveryJournal,
  type RecoveryRecord,
} from "../../../src/protocol/recoveryJournal";
const record: RecoveryRecord = {
  format: 1,
  serviceOrigin: "https://service.example",
  installationId: "10000000-0000-4000-8000-000000000001",
  actorId: "10000000-0000-4000-8000-000000000002",
  attemptId: "10000000-0000-4000-8000-000000000003",
  operationId: "10000000-0000-4000-8000-000000000004",
  subject: "CASE",
  act: "OPEN",
};
it("awaits journal commit before handoff and retains only the explicit identity fields", async () => {
  let commit!: () => void;
  const journal: RecoveryJournal = {
    put: () =>
      new Promise<void>((resolve) => {
        commit = resolve;
      }),
    remove: vi.fn(() => Promise.resolve()),
    list: vi.fn(() => Promise.resolve([])),
  };
  const send = vi.fn(() => Promise.resolve("accepted"));
  const pending = journalBeforeSend(journal, record, () => true, send);
  await Promise.resolve();
  expect(send).not.toHaveBeenCalled();
  commit();
  expect(await pending).toEqual({ kind: "sent", value: "accepted" });
  expect(send).toHaveBeenCalledTimes(1);
  expect(isRecoveryRecord(record)).toBe(true);
  expect(isRecoveryRecord({ ...record, caseReference: "private" })).toBe(false);
});
it("refuses storage failure and stale completion without a transport call", async () => {
  const send = vi.fn(() => Promise.resolve("accepted"));
  const refused: RecoveryJournal = {
    put: vi.fn(() => Promise.reject(new Error("denied"))),
    remove: vi.fn(() => Promise.resolve()),
    list: vi.fn(() => Promise.resolve([])),
  };
  expect(await journalBeforeSend(refused, record, () => true, send)).toEqual({
    kind: "notSent",
    reason: "storage",
  });
  let current = true;
  const changed: RecoveryJournal = {
    ...refused,
    put: vi.fn(() => {
      current = false;
      return Promise.resolve();
    }),
  };
  expect(await journalBeforeSend(changed, record, () => current, send)).toEqual({
    kind: "notSent",
    reason: "stale",
  });
  expect(changed.remove).not.toHaveBeenCalled();
  expect(send).not.toHaveBeenCalled();
});
