import type { Page } from "@playwright/test";

/** Inspect committed native state independently of the application adapter. */
export const recoveryRecords = (page: Page): Promise<Record<string, unknown>[]> =>
  page.evaluate(
    () =>
      new Promise<Record<string, unknown>[]>((resolve, reject) => {
        const request = indexedDB.open("claimcore.recovery", 1);
        request.onupgradeneeded = () => {
          request.transaction?.abort();
          reject(new Error("Expected an existing committed recovery database"));
        };
        request.onerror = () => {
          reject(new Error("Recovery inspection could not open storage"));
        };
        request.onsuccess = () => {
          const database = request.result;
          const tx = database.transaction("attempts", "readonly");
          let records: Record<string, unknown>[] = [];
          const read = tx.objectStore("attempts").getAll();
          read.onsuccess = () => {
            records = read.result as Record<string, unknown>[];
          };
          tx.oncomplete = () => {
            database.close();
            resolve(records);
          };
          const failed = () => {
            database.close();
            reject(new Error("Recovery inspection failed"));
          };
          tx.onabort = failed;
          tx.onerror = failed;
        };
      }),
  );
