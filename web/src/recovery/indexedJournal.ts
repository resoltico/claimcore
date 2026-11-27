import { isCanonicalUuid } from "../protocol/uuid";
import {
  type JournalPublication,
  type JournalIssue,
  type RecoveryJournal,
  type RecoveryRecord,
  type RecoveryScope,
  isRecoveryRecord,
  isRecoveryScope,
  recoveryDatabaseName,
  recoveryTask,
  sameRecoveryScope,
} from "../protocol/recoveryJournal";
const storeName = "attempts";
const confirmedName = "confirmed";
const expiryIndex = "expiry";
const sweepBatch = 32;
const handoffLifetime = 60 * 60 * 1000;
const key = (record: RecoveryRecord): [string, string, string, string] => [
  record.serviceOrigin,
  record.installationId,
  record.actorId,
  record.attemptId,
];
// IndexedDB orders arrays lexicographically, with nested arrays after scalar keys.
const ownerRange = ({ serviceOrigin, installationId, actorId }: RecoveryScope): IDBKeyRange => {
  const prefix = [serviceOrigin, installationId, actorId];
  return IDBKeyRange.bound(prefix, [...prefix, []], false, true);
};
const canonicalOrigin = (value: unknown): boolean => {
  if (typeof value !== "string") {
    return false;
  }
  try {
    const url = new URL(value);
    return (url.protocol === "https:" || url.protocol === "http:") && url.origin === value;
  } catch {
    return false;
  }
};
const validKey = (value: IDBValidKey): value is [string, string, string, string] =>
  Array.isArray(value) &&
  value.length === 4 &&
  canonicalOrigin(value[0]) &&
  isRecoveryScope({ serviceOrigin: value[0], installationId: value[1], actorId: value[2] }) &&
  isCanonicalUuid(value[3]);
const sameAttempt = (left: unknown, right: RecoveryRecord): boolean =>
  isRecoveryRecord(left) &&
  sameRecoveryScope(left, right) &&
  left.attemptId === right.attemptId &&
  left.operationId === right.operationId &&
  left.subject === right.subject &&
  left.act === right.act;
const open = (factory: IDBFactory, name: string): Promise<IDBDatabase> =>
  new Promise((resolve, reject) => {
    const request = factory.open(name, 1);
    let refused = false;
    request.onblocked = () => {
      refused = true;
      reject(new Error("Recovery storage is blocked. Close other ClaimCore pages and retry."));
    };
    request.onerror = () => {
      reject(new Error("Recovery storage is unavailable."));
    };
    request.onupgradeneeded = () => {
      request.result.createObjectStore(storeName);
      request.result.createObjectStore(confirmedName).createIndex(expiryIndex, "until");
    };
    request.onsuccess = () => {
      const database = request.result;
      database.onversionchange = () => {
        database.close();
      };
      if (refused) {
        database.close();
      } else {
        resolve(database);
      }
    };
  });
const transaction = async <T>(
  factory: IDBFactory,
  name: string,
  mode: IDBTransactionMode,
  action: (store: IDBObjectStore, complete: (value: T) => void) => void,
  stores: string[] = [storeName],
): Promise<T> => {
  const database = await open(factory, name);
  try {
    return await new Promise<T>((resolve, reject) => {
      const tx = database.transaction(
        stores,
        mode,
        mode === "readwrite" ? { durability: "strict" } : undefined,
      );
      let value: T;
      tx.oncomplete = () => {
        resolve(value);
      };
      tx.onabort = () => {
        reject(new Error("Recovery storage transaction was not committed."));
      };
      tx.onerror = () => {
        reject(new Error("Recovery storage transaction failed."));
      };
      try {
        action(tx.objectStore(stores[0]!), (result) => {
          value = result;
        });
      } catch {
        tx.abort();
        reject(new Error("Recovery storage is unavailable."));
      }
    });
  } finally {
    database.close();
  }
};
const publishAttempt = (
  store: IDBObjectStore,
  record: RecoveryRecord,
  complete: (result: JournalPublication) => void,
) => {
  if (!isRecoveryRecord(record) || !validKey(key(record)) || recoveryTask(record) === undefined) {
    throw new Error("Invalid recovery identity.");
  }
  const attemptKey = key(record);
  const handoff = store.transaction.objectStore(confirmedName).get(attemptKey);
  handoff.onsuccess = () => {
    if (handoff.result !== undefined) {
      store.transaction.abort();
      return;
    }
    const existing = store.get(attemptKey);
    existing.onsuccess = () => {
      const value: unknown = existing.result;
      if (value === undefined) {
        const writing = store.add(record, attemptKey);
        writing.onsuccess = () => {
          complete("inserted");
        };
      } else if (sameAttempt(value, record)) {
        complete("existing");
      } else {
        store.transaction.abort();
      }
    };
  };
};
const removeAttempt = (store: IDBObjectStore, record: RecoveryRecord, complete: () => void) => {
  const existing = store.get(key(record));
  existing.onsuccess = () => {
    const value: unknown = existing.result;
    if (value === undefined) {
      complete();
    } else if (sameAttempt(value, record)) {
      const removing = store.delete(key(record));
      removing.onsuccess = complete;
    } else {
      store.transaction.abort();
    }
  };
};
const listAttempts = (
  store: IDBObjectStore,
  scope: RecoveryScope,
  complete: (records: ReadonlyArray<RecoveryRecord>) => void,
  issue: ((issue: JournalIssue) => void) | undefined,
) => {
  const records: RecoveryRecord[] = [];
  const request = store.openCursor(ownerRange(scope));
  request.onsuccess = () => {
    const cursor = request.result;
    if (cursor === null) {
      complete(records);
      return;
    }
    if (!validKey(cursor.key)) {
      issue?.("damaged");
      cursor.continue();
      return;
    }
    const [serviceOrigin, installationId, actorId, attemptId] = cursor.key;
    if (sameRecoveryScope(scope, { serviceOrigin, installationId, actorId })) {
      const value: unknown = cursor.value;
      if (
        !isRecoveryRecord(value) ||
        !sameRecoveryScope(scope, value) ||
        value.attemptId !== attemptId
      ) {
        issue?.("damaged");
      } else {
        records.push(value);
      }
    }
    cursor.continue();
  };
};
const confirmation = (value: unknown): { record: RecoveryRecord; until: number } | null => {
  if (
    typeof value !== "object" ||
    value === null ||
    !("record" in value) ||
    !isRecoveryRecord(value.record) ||
    !("until" in value) ||
    typeof value.until !== "number" ||
    !Number.isSafeInteger(value.until)
  ) {
    return null;
  }
  return { record: value.record, until: value.until };
};

const retainConfirmation = (
  store: IDBObjectStore,
  record: RecoveryRecord,
  complete: () => void,
) => {
  const lookup = store.get(key(record));
  lookup.onsuccess = () => {
    if (lookup.result === undefined) {
      complete();
      return;
    }
    if (!sameAttempt(lookup.result, record)) {
      store.transaction.abort();
      return;
    }
    const confirmed = store.transaction.objectStore(confirmedName);
    const existing = confirmed.get(key(record));
    existing.onsuccess = () => {
      const value: unknown = existing.result;
      if (value === undefined) {
        confirmed.add({ record, until: Date.now() + handoffLifetime }, key(record));
      } else if (!sameAttempt(confirmation(value)?.record, record)) {
        store.transaction.abort();
        return;
      }
      store.delete(key(record)).onsuccess = complete;
    };
  };
};

const listConfirmations = (
  store: IDBObjectStore,
  scope: RecoveryScope,
  complete: (records: RecoveryRecord[]) => void,
  issue: ((issue: JournalIssue) => void) | undefined,
) => {
  const results: RecoveryRecord[] = [];
  const request = store.openCursor(ownerRange(scope));
  request.onsuccess = () => {
    const cursor = request.result;
    if (cursor === null) {
      complete(results);
      return;
    }
    if (!validKey(cursor.key)) {
      issue?.("damaged");
      cursor.continue();
      return;
    }
    const [serviceOrigin, installationId, actorId, attemptId] = cursor.key;
    if (sameRecoveryScope(scope, { serviceOrigin, installationId, actorId })) {
      const value = confirmation(cursor.value);
      if (
        value === null ||
        !sameRecoveryScope(scope, value.record) ||
        value.record.attemptId !== attemptId
      ) {
        issue?.("damaged");
      } else if (value.until > Date.now()) {
        results.push(value.record);
      }
    }
    cursor.continue();
  };
};

/** Per-attempt transactions neither overwrite nor clear another pending identity. */
export const createIndexedJournal = (
  factory: IDBFactory,
  name = recoveryDatabaseName,
): RecoveryJournal => ({
  put: (record) =>
    transaction<JournalPublication>(
      factory,
      name,
      "readwrite",
      (store, complete) => {
        publishAttempt(store, record, complete);
      },
      [storeName, confirmedName],
    ),
  remove: (record) =>
    transaction<void>(factory, name, "readwrite", (store, complete) => {
      removeAttempt(store, record, () => {
        complete(undefined);
      });
    }),
  confirm: (record) =>
    transaction<void>(
      factory,
      name,
      "readwrite",
      (store, complete) => {
        retainConfirmation(store, record, () => {
          complete(undefined);
        });
      },
      [storeName, confirmedName],
    ),
  confirmed: (scope, issue) =>
    transaction<ReadonlyArray<RecoveryRecord>>(
      factory,
      name,
      "readonly",
      (store, complete) => {
        listConfirmations(store, scope, complete, issue);
      },
      [confirmedName],
    ),
  list: (scope, issue) =>
    transaction<ReadonlyArray<RecoveryRecord>>(factory, name, "readonly", (store, complete) => {
      listAttempts(store, scope, complete, issue);
    }),
});
const changedEvent = "claimcore-recovery-changed";
const publish = () => {
  try {
    globalThis.dispatchEvent(new Event(changedEvent));
    if (typeof BroadcastChannel !== "undefined") {
      const channel = new BroadcastChannel(changedEvent);
      try {
        channel.postMessage("changed");
      } finally {
        channel.close();
      }
    }
  } catch {
    /* Notifications cannot change a committed journal outcome. */
  }
};
export const subscribeRecovery = (listener: () => void): (() => void) => {
  globalThis.addEventListener(changedEvent, listener);
  globalThis.addEventListener("focus", listener);
  let channel: BroadcastChannel | null = null;
  try {
    if (typeof BroadcastChannel !== "undefined") {
      channel = new BroadcastChannel(changedEvent);
      channel.onmessage = listener;
    }
  } catch {
    /* Focus and same-page notifications remain available. */
  }
  return () => {
    globalThis.removeEventListener(changedEvent, listener);
    globalThis.removeEventListener("focus", listener);
    channel?.close();
  };
};
// Expiry visits a bounded number of indexed candidates, even for inactive owners.
let expiryProgress: { until: IDBValidKey; primary: IDBValidKey } | undefined;

const expireHandoffs = (factory: IDBFactory) =>
  transaction<void>(
    factory,
    recoveryDatabaseName,
    "readwrite",
    (store, complete) => {
      const now = Date.now();
      const previous = expiryProgress;
      const bounds =
        previous === undefined || factory.cmp(previous.until, now) > 0
          ? IDBKeyRange.upperBound(now)
          : IDBKeyRange.bound(previous.until, now);
      const request = store.index(expiryIndex).openCursor(bounds);
      let remaining = sweepBatch;
      request.onsuccess = () => {
        const cursor = request.result;
        if (cursor === null) {
          expiryProgress = undefined;
          complete(undefined);
          return;
        }
        if (remaining === 0) {
          complete(undefined);
          return;
        }
        if (previous !== undefined && factory.cmp(cursor.key, previous.until) === 0) {
          const order = factory.cmp(cursor.primaryKey, previous.primary);
          if (order < 0) {
            cursor.continuePrimaryKey(previous.until, previous.primary);
            return;
          }
          if (order === 0) {
            cursor.continue();
            return;
          }
        }
        remaining -= 1;
        expiryProgress = { until: cursor.key, primary: cursor.primaryKey };
        const admitted = confirmation(cursor.value);
        if (
          admitted !== null &&
          validKey(cursor.primaryKey) &&
          factory.cmp(key(admitted.record), cursor.primaryKey) === 0
        ) {
          cursor.delete();
        }
        cursor.continue();
      };
    },
    [confirmedName],
  );

const diagnosticProgress = new Map<string, IDBValidKey>();
const scanKeys = (factory: IDBFactory, name: string) =>
  transaction<boolean>(
    factory,
    recoveryDatabaseName,
    "readonly",
    (store, complete) => {
      const last = diagnosticProgress.get(name);
      const request = store.openKeyCursor(
        last === undefined ? undefined : IDBKeyRange.lowerBound(last, true),
      );
      let remaining = sweepBatch;
      let damaged = false;
      request.onsuccess = () => {
        const cursor = request.result;
        if (cursor === null) {
          diagnosticProgress.delete(name);
          complete(damaged);
        } else if (remaining === 0) {
          complete(damaged);
        } else {
          remaining -= 1;
          diagnosticProgress.set(name, cursor.primaryKey);
          damaged ||= !validKey(cursor.primaryKey);
          cursor.continue();
        }
      };
    },
    [name],
  );

let sweeping = false;
let unscopableEvidence = false;

export const browserJournal: RecoveryJournal = {
  put: async (record) => {
    const publication = await createIndexedJournal(globalThis.indexedDB).put(record);
    publish();
    return publication;
  },
  remove: (record) => createIndexedJournal(globalThis.indexedDB).remove(record).then(publish),
  confirm: (record) => createIndexedJournal(globalThis.indexedDB).confirm!(record).then(publish),
  confirmed: (scope, issue) => createIndexedJournal(globalThis.indexedDB).confirmed!(scope, issue),
  list: (scope, issue) => createIndexedJournal(globalThis.indexedDB).list(scope, issue),
  diagnose: async (issue) => {
    const factory = globalThis.indexedDB;
    if (factory === undefined) {
      return;
    }
    if (!sweeping) {
      sweeping = true;
      void expireHandoffs(factory)
        .catch(() => {
          // Housekeeping does not gate discovery or publication.
        })
        .finally(() => {
          sweeping = false;
        });
    }
    const [attempts, confirmations, databases] = await Promise.all([
      scanKeys(factory, storeName),
      scanKeys(factory, confirmedName),
      Promise.resolve().then(() => factory.databases()),
    ]);
    unscopableEvidence ||= attempts || confirmations;
    if (unscopableEvidence) {
      issue("unscoped");
    }
    if (
      databases.some(
        ({ name }) =>
          name === "claimcore.operation-recovery.3" ||
          name === "claimcore.operation-recovery" ||
          name === "claimcore.recovery",
      )
    ) {
      issue("earlierFormat");
    }
  },
};
