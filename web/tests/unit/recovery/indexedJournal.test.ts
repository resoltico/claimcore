import { expect, it, vi } from "vitest";
import { createIndexedJournal } from "../../../src/recovery/indexedJournal";
import { journalBeforeSend } from "../../../src/protocol/recoveryJournal";
import { recoveryRecord } from "../../support/recovery.fixtures";
/** Controllable native event boundary; real browser storage is qualified in e2e. */
const events = () => {
  const request = { onsuccess: null, onerror: null } as unknown as IDBRequest;
  const lookup = { result: undefined, onsuccess: null } as unknown as IDBRequest;
  const store = {
    get: vi.fn(() => lookup),
    add: vi.fn(() => request),
  } as unknown as IDBObjectStore;
  const tx = {
    oncomplete: null,
    onerror: null,
    onabort: null,
    objectStore: () => store,
    abort: vi.fn(),
  } as unknown as IDBTransaction;
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
  return { request, lookup, tx, close, database, opening, factory };
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
  native.lookup.onsuccess?.(new Event("success"));
  native.request.onsuccess?.(new Event("success"));
  await Promise.resolve();
  expect(send).not.toHaveBeenCalled();
  native.tx.oncomplete?.(new Event("complete"));
  expect(await prepared).toEqual({ kind: "sent", value: "sent" });
  expect(send).toHaveBeenCalledTimes(1);
  expect(native.close).toHaveBeenCalledTimes(1);
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
