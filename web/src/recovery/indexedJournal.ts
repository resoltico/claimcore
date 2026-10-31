import {
  type RecoveryJournal,
  type RecoveryRecord,
  type RecoveryScope,
  isRecoveryRecord,
  sameRecoveryScope,
} from "../protocol/recoveryJournal";
const storeName = "attempts";
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
): Promise<T> => {
  const database = await open(factory, name);
  try {
    return await new Promise<T>((resolve, reject) => {
      const tx = database.transaction(storeName, mode);
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
        action(tx.objectStore(storeName), (result) => {
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
const publishAttempt = (store: IDBObjectStore, record: RecoveryRecord, complete: () => void) => {
  if (!isRecoveryRecord(record)) {
    throw new Error("Invalid recovery identity.");
  }
  const existing = store.get(key(record));
  existing.onsuccess = () => {
    const value: unknown = existing.result;
    if (value === undefined) {
      const writing = store.add(record, key(record));
      writing.onsuccess = complete;
    } else if (sameAttempt(value, record)) {
      complete();
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
/** Per-attempt transactions neither overwrite nor clear another pending identity. */
export const createIndexedJournal = (
  factory: IDBFactory,
  name = "claimcore.recovery",
): RecoveryJournal => ({
  put: (record) =>
    transaction<void>(factory, name, "readwrite", (store, complete) => {
      publishAttempt(store, record, () => {
        complete(undefined);
      });
    }),
  remove: (record) =>
    transaction<void>(factory, name, "readwrite", (store, complete) => {
      removeAttempt(store, record, () => {
        complete(undefined);
      });
    }),
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
  put: (record) => createIndexedJournal(globalThis.indexedDB).put(record).then(publish),
  remove: (record) => createIndexedJournal(globalThis.indexedDB).remove(record).then(publish),
  list: (scope) => createIndexedJournal(globalThis.indexedDB).list(scope),
};
