import { expect, it, vi } from "vitest";
import { createIndexedJournal } from "../../../src/recovery/indexedJournal";
import { journalBeforeSend } from "../../../src/protocol/recoveryJournal";
import { recoveryRecord } from "../../support/recovery.fixtures";
/** Controllable native event boundary; real browser storage is qualified in e2e. */
const events = (stored?: unknown, confirmed?: unknown) => {
  const request = { onsuccess: null, onerror: null } as unknown as IDBRequest;
  const lookup = { result: stored, onsuccess: null } as unknown as IDBRequest;
  const handoff = { result: confirmed, onsuccess: null } as unknown as IDBRequest;
  const add = vi.fn(() => request);
  const remove = vi.fn(() => request);
  const abort = vi.fn();
  const nativeTransaction = {
    oncomplete: null,
    onerror: null,
    onabort: null,
    abort,
  };
  const store = {
    get: vi.fn(() => lookup),
    add,
    delete: remove,
    transaction: nativeTransaction,
  } as unknown as IDBObjectStore;
  const tx = Object.assign(nativeTransaction, {
    objectStore: (name: string) => (name === "attempts" ? store : { get: () => handoff }),
  }) as unknown as IDBTransaction;
  const close = vi.fn();
  const database = {
    transaction: () => tx,
    close,
    onversionchange: null,
    createObjectStore: vi.fn(),
  } as unknown as IDBDatabase;
  const opening = {
    result: database,
    onblocked: null,
    onerror: null,
    onsuccess: null,
    onupgradeneeded: null,
  } as unknown as IDBOpenDBRequest;
  const factory = { open: () => opening } as unknown as IDBFactory;
  return { request, lookup, handoff, add, remove, abort, tx, close, database, opening, factory };
};
it("an individual write success cannot permit dispatch before transaction completion", async () => {
  const native = events();
  const send = vi.fn(() => Promise.resolve("sent"));
  const prepared = journalBeforeSend(
    createIndexedJournal(native.factory),
    recoveryRecord(),
    () => true,
    send,
  );
  native.opening.onsuccess?.(new Event("success"));
  await Promise.resolve();
  native.handoff.onsuccess?.(new Event("success"));
  native.lookup.onsuccess?.(new Event("success"));
  native.request.onsuccess?.(new Event("success"));
  await Promise.resolve();
  expect(send).not.toHaveBeenCalled();
  native.tx.oncomplete?.(new Event("complete"));
  expect(await prepared).toEqual({ kind: "sent", publication: "inserted", value: "sent" });
  expect(send).toHaveBeenCalledTimes(1);
  expect(native.close).toHaveBeenCalledTimes(1);
});
it("an exact existing record is reported only when its transaction completes", async () => {
  const record = recoveryRecord();
  const native = events(record);
  const send = vi.fn(() => Promise.resolve("sent"));
  const prepared = journalBeforeSend(
    createIndexedJournal(native.factory),
    record,
    () => true,
    send,
  );
  native.opening.onsuccess?.(new Event("success"));
  await Promise.resolve();
  native.handoff.onsuccess?.(new Event("success"));
  native.lookup.onsuccess?.(new Event("success"));
  await Promise.resolve();
  expect(native.add).not.toHaveBeenCalled();
  expect(send).not.toHaveBeenCalled();
  native.tx.oncomplete?.(new Event("complete"));
  expect(await prepared).toEqual({ kind: "sent", publication: "existing", value: "sent" });
});
it("conditional removal refuses a different identity at the same exact key", async () => {
  const record = recoveryRecord();
  const native = events({ ...record, operationId: "10000000-0000-4000-8000-000000000099" });
  const removed = createIndexedJournal(native.factory).remove(record);
  native.opening.onsuccess?.(new Event("success"));
  await Promise.resolve();
  native.handoff.onsuccess?.(new Event("success"));
  native.lookup.onsuccess?.(new Event("success"));
  expect(native.abort).toHaveBeenCalledTimes(1);
  expect(native.remove).not.toHaveBeenCalled();
  native.tx.onabort?.(new Event("abort"));
  await expect(removed).rejects.toThrow("Recovery storage transaction was not committed.");
});
it("a write success followed by transaction abort is NOT_SENT", async () => {
  const native = events();
  const send = vi.fn(() => Promise.resolve("sent"));
  const prepared = journalBeforeSend(
    createIndexedJournal(native.factory),
    recoveryRecord(),
    () => true,
    send,
  );
  native.opening.onsuccess?.(new Event("success"));
  await Promise.resolve();
  native.handoff.onsuccess?.(new Event("success"));
  native.lookup.onsuccess?.(new Event("success"));
  native.request.onsuccess?.(new Event("success"));
  native.tx.onabort?.(new Event("abort"));
  expect(await prepared).toEqual({ kind: "notSent", reason: "storage" });
  expect(send).not.toHaveBeenCalled();
  expect(native.close).toHaveBeenCalledTimes(1);
});
it("blocked opens refuse preparation and close a late connection", async () => {
  const native = events();
  const send = vi.fn(() => Promise.resolve("sent"));
  const prepared = journalBeforeSend(
    createIndexedJournal(native.factory),
    recoveryRecord(),
    () => true,
    send,
  );
  native.opening.onblocked?.(new Event("blocked") as IDBVersionChangeEvent);
  expect(await prepared).toEqual({ kind: "notSent", reason: "storage" });
  native.opening.onsuccess?.(new Event("success"));
  expect(native.close).toHaveBeenCalledTimes(1);
  expect(send).not.toHaveBeenCalled();
});

it("preserves a conflicting handoff and refuses before dispatch", async () => {
  const record = recoveryRecord();
  const native = events(undefined, { record: recoveryRecord(), until: Date.now() + 1000 });
  const send = vi.fn(() => Promise.resolve("sent"));
  const prepared = journalBeforeSend(
    createIndexedJournal(native.factory),
    record,
    () => true,
    send,
  );
  native.opening.onsuccess?.(new Event("success"));
  await Promise.resolve();
  native.handoff.onsuccess?.(new Event("success"));
  expect(native.abort).toHaveBeenCalledOnce();
  native.tx.onabort?.(new Event("abort"));
  expect(await prepared).toEqual({ kind: "notSent", reason: "storage" });
  expect(send).not.toHaveBeenCalled();
  expect(native.add).not.toHaveBeenCalled();
});
