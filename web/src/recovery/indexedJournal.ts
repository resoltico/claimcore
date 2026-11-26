import {
  type JournalPublication,
  type RecoveryJournal,
  type RecoveryRecord,
  type RecoveryScope,
  isRecoveryRecord,
  sameRecoveryScope,
} from "../protocol/recoveryJournal";
const storeName = "attempts";
const confirmedName = "confirmed";
const handoffLifetime = 60 * 60 * 1000;
const key = (record: RecoveryRecord): [string, string, string, string] => [
  record.serviceOrigin,
  record.installationId,
  record.actorId,
  record.attemptId,
];
const validKey = (value: IDBValidKey): value is [string, string, string, string] =>
  Array.isArray(value) &&
  value.length === 4 &&
  value.every((part: unknown) => typeof part === "string");
const sameAttempt = (left: unknown, right: RecoveryRecord): boolean =>
  isRecoveryRecord(left) &&
  sameRecoveryScope(left, right) &&
  left.attemptId === right.attemptId &&
  left.operationId === right.operationId &&
  left.subject === right.subject &&
  left.act === right.act;
const open = (factory: IDBFactory, name: string): Promise<IDBDatabase> =>
  new Promise((resolve, reject) => {
    const request = factory.open(name, 2);
    let refused = false;
    request.onblocked = () => {
      refused = true;
      reject(new Error("Recovery storage is blocked. Close other ClaimCore pages and retry."));
    };
    request.onerror = () => {
      reject(new Error("Recovery storage is unavailable."));
    };
    request.onupgradeneeded = (event) => {
      if (event.oldVersion === 0) {
        request.result.createObjectStore(storeName);
      }
      request.result.createObjectStore(confirmedName);
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
      const tx = database.transaction(stores, mode);
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
  if (!isRecoveryRecord(record)) {
    throw new Error("Invalid recovery identity.");
  }
  const existing = store.get(key(record));
  existing.onsuccess = () => {
    const value: unknown = existing.result;
    if (value === undefined) {
      const writing = store.add(record, key(record));
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
) => {
  const records: RecoveryRecord[] = [];
  const request = store.openCursor();
  request.onsuccess = () => {
    const cursor = request.result;
    if (cursor === null) {
      complete(records);
      return;
    }
    if (!validKey(cursor.key)) {
      store.transaction.abort();
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
        store.transaction.abort();
        return;
      }
      records.push(value);
    }
    cursor.continue();
  };
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
    confirmed.put({ record, until: Date.now() + handoffLifetime }, key(record));
    store.delete(key(record)).onsuccess = complete;
  };
};

const listConfirmations = (
  store: IDBObjectStore,
  scope: RecoveryScope,
  complete: (records: RecoveryRecord[]) => void,
) => {
  const results: RecoveryRecord[] = [];
  const request = store.openCursor();
  request.onsuccess = () => {
    const cursor = request.result;
    if (cursor === null) {
      complete(results);
      return;
    }
    const value: unknown = cursor.value;
    if (
      typeof value !== "object" ||
      value === null ||
      !("record" in value) ||
      !isRecoveryRecord(value.record) ||
      !("until" in value) ||
      typeof value.until !== "number" ||
      !validKey(cursor.key) ||
      JSON.stringify(key(value.record)) !== JSON.stringify(cursor.key)
    ) {
      store.transaction.abort();
      return;
    }
    if (value.until <= Date.now()) {
      cursor.delete();
    } else if (sameRecoveryScope(scope, value.record)) {
      results.push(value.record);
    }
    cursor.continue();
  };
};

/** Per-attempt transactions neither overwrite nor clear another pending identity. */
export const createIndexedJournal = (
  factory: IDBFactory,
  name = "claimcore.recovery",
): RecoveryJournal => ({
  put: (record) =>
    transaction<JournalPublication>(factory, name, "readwrite", (store, complete) => {
      publishAttempt(store, record, complete);
    }),
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
  confirmed: (scope) =>
    transaction<ReadonlyArray<RecoveryRecord>>(
      factory,
      name,
      "readwrite",
      (store, complete) => {
        listConfirmations(store, scope, complete);
      },
      [confirmedName],
    ),
  list: (scope) =>
    transaction<ReadonlyArray<RecoveryRecord>>(factory, name, "readonly", (store, complete) => {
      listAttempts(store, scope, complete);
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
export const browserJournal: RecoveryJournal = {
  put: async (record) => {
    const publication = await createIndexedJournal(globalThis.indexedDB).put(record);
    publish();
    return publication;
  },
  remove: (record) => createIndexedJournal(globalThis.indexedDB).remove(record).then(publish),
  confirm: (record) => createIndexedJournal(globalThis.indexedDB).confirm!(record).then(publish),
  confirmed: (scope) => createIndexedJournal(globalThis.indexedDB).confirmed!(scope),
  list: (scope) => createIndexedJournal(globalThis.indexedDB).list(scope),
};
