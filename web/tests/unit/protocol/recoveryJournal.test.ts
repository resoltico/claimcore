import { operationSettlement } from "@contracts/client";
import settlements from "../../../../tests/fixtures/operation-settlement.json" with { type: "json" };
import { expect, it, vi } from "vitest";
import {
  journalBeforeSend,
  recoveryTask,
  isRecoveryRecord,
  type JournalPublication,
  type RecoveryJournal,
  type RecoveryRecord,
} from "../../../src/protocol/recoveryJournal";
const record: RecoveryRecord = {
  format: 3,
  serviceOrigin: "https://service.example",
  installationId: "10000000-0000-4000-8000-000000000001",
  actorId: "10000000-0000-4000-8000-000000000002",
  attemptId: "10000000-0000-4000-8000-000000000003",
  operationId: "10000000-0000-4000-8000-000000000004",
  subject: "CASE",
  act: "case.open",
};
it("awaits journal commit before handoff and retains only the explicit identity fields", async () => {
  let commit!: () => void;
  const journal: RecoveryJournal = {
    put: () =>
      new Promise<JournalPublication>((resolve) => {
        commit = () => {
          resolve("inserted");
        };
      }),
    remove: vi.fn(() => Promise.resolve()),
    list: vi.fn(() => Promise.resolve([])),
  };
  const send = vi.fn(() => Promise.resolve("accepted"));
  const pending = journalBeforeSend(journal, record, () => true, send);
  await Promise.resolve();
  expect(send).not.toHaveBeenCalled();
  commit();
  expect(await pending).toEqual({ kind: "sent", publication: "inserted", value: "accepted" });
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
      return Promise.resolve<JournalPublication>("inserted");
    }),
  };
  expect(await journalBeforeSend(changed, record, () => current, send)).toEqual({
    kind: "notSent",
    reason: "stale",
  });
  expect(changed.remove).not.toHaveBeenCalled();
  expect(send).not.toHaveBeenCalled();
});

it("admits recovers identity independently of current task captions", () => {
  expect(isRecoveryRecord({ ...record, subject: "WORK", act: "work.update" })).toBe(true);
  expect(
    isRecoveryRecord({ ...record, subject: "REMINDER", act: "reminder.completeAndPlanNext" }),
  ).toBe(true);
  expect(isRecoveryRecord({ ...record, format: 1 })).toBe(false);
  expect(recoveryTask({ ...record, subject: "WORK" })).toBeUndefined();
  expect(isRecoveryRecord({ ...record, act: "case.retired" })).toBe(true);
  for (const privateValue of [
    { action: "private" },
    { dueDate: "2026-11-10" },
    { handlingSummary: "private" },
    { incidentDescription: "private" },
    { body: "private" },
  ]) {
    expect(isRecoveryRecord({ ...record, ...privateValue })).toBe(false);
  }
});

it("admits the authored shared operation settlement cases", () => {
  expect(settlements).toHaveLength(8);
  for (const expected of settlements) {
    const settlement =
      operationSettlement[expected.observation as keyof typeof operationSettlement];
    expect(settlement).toBe(expected.settlement);
    expect(settlement === "RECORDED" || settlement === "NOT_RECORDED").toBe(expected.final);
  }
});
