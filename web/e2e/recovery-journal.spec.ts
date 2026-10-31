import { randomUUID } from "node:crypto";
import { readFile } from "node:fs/promises";
import { stripTypeScriptTypes } from "node:module";
import { expect, test, type Page } from "@playwright/test";
import type * as IndexedJournalModule from "../src/recovery/indexedJournal";
import type * as RecoveryModule from "../src/protocol/recoveryJournal";
import type { RecoveryRecord, RecoveryScope } from "../src/protocol/recoveryJournal";
import { openAuthenticated, browserRequest, sessionToken } from "./session-helpers";
import { startOpen, reviewDraft, openCase, startAct } from "./case-workflow";
import { isResponse } from "../src/protocol/client";

/** Serve the actual adapter source in this isolated test page, without a product debug endpoint. */
const adapter = async (page: Page) => {
  for (const [path, file] of [
    ["**/__claimcore_qa__/recovery/indexedJournal.js", "../src/recovery/indexedJournal.ts"],
    ["**/__claimcore_qa__/protocol/recoveryJournal", "../src/protocol/recoveryJournal.ts"],
  ] as const) {
    const source = await readFile(new URL(file, import.meta.url), "utf8");
    const script = stripTypeScriptTypes(source);
    await page.route(path, (route) =>
      route.fulfill({ contentType: "text/javascript", body: script }),
    );
  }
};
const scopeFor = async (page: Page): Promise<RecoveryScope> => {
  const csrf = await sessionToken(page);
  const reply = await browserRequest(page, "/api/context", {
    method: "POST",
    headers: { "Content-Type": "application/json", "X-ClaimCore-Antiforgery": csrf },
    body: "{}",
  });
  if (!isResponse("context", reply.payload) || reply.payload.outcome.kind !== "SUCCEEDED") {
    throw new Error("Recovery scope positive control");
  }
  return {
    serviceOrigin: new URL(page.url()).origin,
    actorId: reply.payload.outcome.data.actorId,
    installationId: reply.payload.outcome.data.installationId,
  };
};
const records = (page: Page, scope: RecoveryScope) =>
  page.evaluate(async (selectedScope) => {
    const path = "/__claimcore_qa__/recovery/indexedJournal.js";
    const module = (await import(path)) as typeof IndexedJournalModule;
    return module.createIndexedJournal(indexedDB).list(selectedScope);
  }, scope);
const keys = [
  "act",
  "actorId",
  "attemptId",
  "format",
  "installationId",
  "operationId",
  "serviceOrigin",
  "subject",
];

test("real IndexedDB keeps independent attempts across pages with one operation ID", async ({
  page,
  context,
}) => {
  await openAuthenticated(page);
  await adapter(page);
  const scope = await scopeFor(page);
  const second = await context.newPage();
  await openAuthenticated(second);
  await adapter(second);
  const operationId = randomUUID();
  const firstRecord: RecoveryRecord = {
    ...scope,
    format: 1,
    operationId,
    attemptId: randomUUID(),
    subject: "CASE",
    act: "CLOSE",
  };
  const secondRecord: RecoveryRecord = { ...firstRecord, attemptId: randomUUID() };
  const put = (target: Page, record: RecoveryRecord) =>
    target.evaluate(async (inputRecord) => {
      const path = "/__claimcore_qa__/recovery/indexedJournal.js";
      const module = (await import(path)) as typeof IndexedJournalModule;
      await module.createIndexedJournal(indexedDB).put(inputRecord);
    }, record);
  await Promise.all([put(page, firstRecord), put(second, secondRecord)]);
  expect((await records(page, scope)).map((record) => record.attemptId).sort()).toEqual(
    [firstRecord.attemptId, secondRecord.attemptId].sort(),
  );
  await page.evaluate(async (record) => {
    const path = "/__claimcore_qa__/recovery/indexedJournal.js";
    const module = (await import(path)) as typeof IndexedJournalModule;
    await module.createIndexedJournal(indexedDB).remove(record);
  }, firstRecord);
  expect(await records(second, scope)).toEqual([secondRecord]);
  expect(await records(second, { ...scope, actorId: randomUUID() })).toEqual([]);
  expect(await records(second, { ...scope, installationId: randomUUID() })).toEqual([]);
  await second.reload();
  await second.getByRole("button", { name: "Check unresolved operation", exact: true }).click();
  await expect.poll(() => records(page, scope)).toEqual([]);
  await second.close();
});

test("tab loss after transaction commit and before notification leaves identity for final resolution", async ({
  page,
  context,
}) => {
  await openAuthenticated(page);
  await startOpen(page, `JOURNAL-COMMIT-${randomUUID()}`);
  const identity = await reviewDraft(page);
  // Inject loss of the native completion notification after the real transaction has committed.
  await page.evaluate(() => {
    const completion = Object.getOwnPropertyDescriptor(IDBTransaction.prototype, "oncomplete");
    if (completion?.set === undefined || completion.get === undefined) {
      throw new Error("Native transaction completion setter is unavailable");
    }
    const state = globalThis as unknown as { journalCommitObserved: boolean };
    state.journalCommitObserved = false;
    Object.defineProperty(IDBTransaction.prototype, "oncomplete", {
      configurable: true,
      get(this: IDBTransaction) {
        return completion.get!.call(this) as IDBTransaction["oncomplete"];
      },
      set(this: IDBTransaction, handler: IDBTransaction["oncomplete"]) {
        if (this.mode === "readwrite" && this.objectStoreNames.contains("attempts")) {
          this.addEventListener(
            "complete",
            () => {
              state.journalCommitObserved = true;
            },
            { once: true },
          );
        } else {
          completion.set!.call(this, handler);
        }
      },
    });
  });
  let executions = 0;
  page.on("request", (request) => {
    if (request.url().endsWith("/api/operation.execute")) {
      executions += 1;
    }
  });
  await page.getByText("I confirm these changes.", { exact: true }).click();
  await page.getByRole("button", { name: "Record changes", exact: true }).click();
  await expect
    .poll(() =>
      page.evaluate(
        () => (globalThis as unknown as { journalCommitObserved: boolean }).journalCommitObserved,
      ),
    )
    .toBe(true);
  expect(executions).toBe(0);
  const replacement = await context.newPage();
  await page.close();
  await openAuthenticated(replacement);
  await adapter(replacement);
  const scope = await scopeFor(replacement);
  const saved = await records(replacement, scope);
  expect(saved).toHaveLength(1);
  expect(saved[0]?.operationId).toBe(identity.operationId);
  expect(Object.keys(saved[0]!).sort()).toEqual(keys);
  const resolved = replacement.waitForResponse((response) =>
    response.url().endsWith("/api/operation.resolve"),
  );
  await replacement
    .getByRole("button", { name: "Check unresolved operation", exact: true })
    .click();
  const reply: unknown = await (await resolved).json();
  expect(isResponse("operation.resolve", reply) && reply.outcome.kind === "NOT_ACCEPTED").toBe(
    true,
  );
  await expect.poll(() => records(replacement, scope)).toEqual([]);
  expect(executions).toBe(0);
  await replacement.close();
});

test("a lost accepted reply survives tab closure and resolves without resubmission", async ({
  page,
  context,
}) => {
  await openAuthenticated(page);
  await openCase(page, `JOURNAL-ACCEPTED-${randomUUID()}`);
  await adapter(page);
  const scope = await scopeFor(page);
  await startAct(page, "Close the case");
  const identity = await reviewDraft(page);
  let executions = 0;
  await page.route("**/api/operation.execute", async (route) => {
    executions += 1;
    const saved = await records(page, scope);
    expect(saved).toHaveLength(1);
    expect(saved[0]?.operationId).toBe(identity.operationId);
    expect(Object.keys(saved[0]!).sort()).toEqual(keys);
    const accepted = await route.fetch({ maxRedirects: 0 });
    const value: unknown = await accepted.json();
    expect(isResponse("operation.execute", value) && value.outcome.kind === "ACCEPTED").toBe(true);
    await route.abort("connectionfailed");
  });
  await page.getByText("I confirm these changes.", { exact: true }).click();
  await page.getByRole("button", { name: "Record changes", exact: true }).click();
  await expect(page.getByRole("button", { name: "Send again", exact: true })).toBeVisible();
  const replacement = await context.newPage();
  await page.close();
  await openAuthenticated(replacement);
  await adapter(replacement);
  const resolved = replacement.waitForResponse((response) =>
    response.url().endsWith("/api/operation.resolve"),
  );
  await replacement
    .getByRole("button", { name: "Check unresolved operation", exact: true })
    .click();
  const value: unknown = await (await resolved).json();
  expect(
    isResponse("operation.resolve", value) &&
      value.outcome.kind === "ACCEPTED" &&
      value.outcome.data.operationId === identity.operationId,
  ).toBe(true);
  await expect.poll(() => records(replacement, scope)).toEqual([]);
  expect(executions).toBe(1);
  await replacement.close();
});

test("a real blocked open refuses preparation and retains the earlier record", async ({
  page,
  context,
}) => {
  await openAuthenticated(page);
  await adapter(page);
  const scope = await scopeFor(page);
  const earlier: RecoveryRecord = {
    ...scope,
    format: 1,
    operationId: randomUUID(),
    attemptId: randomUUID(),
    subject: "CASE",
    act: "CLOSE",
  };
  await page.evaluate(async (record) => {
    const path = "/__claimcore_qa__/recovery/indexedJournal.js";
    const module = (await import(path)) as typeof IndexedJournalModule;
    await module.createIndexedJournal(indexedDB).put(record);
  }, earlier);
  const holder = await context.newPage();
  await openAuthenticated(holder);
  await holder.evaluate(
    () =>
      new Promise<void>((resolve, reject) => {
        const request = indexedDB.open("claimcore.recovery", 1);
        request.onerror = () => {
          reject(new Error("Hold connection failed"));
        };
        request.onsuccess = () => {
          (globalThis as unknown as { heldJournal: IDBDatabase }).heldJournal = request.result;
          request.result.onversionchange = () => undefined;
          resolve();
        };
      }),
  );
  const blocked = await page.evaluate(async (selectedScope) => {
    const adapterPath = "/__claimcore_qa__/recovery/indexedJournal.js";
    const protocolPath = "/__claimcore_qa__/protocol/recoveryJournal";
    const journalModule = (await import(adapterPath)) as typeof IndexedJournalModule;
    const protocol = (await import(protocolPath)) as typeof RecoveryModule;
    // The injected factory requests an upgrade so the browser emits a genuine blocked event.
    const factory = { open: (name: string) => indexedDB.open(name, 2) } as IDBFactory;
    let dispatched = false;
    const record: RecoveryRecord = {
      ...selectedScope,
      format: 1,
      operationId: crypto.randomUUID(),
      attemptId: crypto.randomUUID(),
      subject: "CASE",
      act: "OPEN",
    };
    const prepared = await protocol.journalBeforeSend(
      journalModule.createIndexedJournal(factory),
      record,
      () => true,
      () => {
        dispatched = true;
        return Promise.resolve();
      },
    );
    return { result: prepared, dispatched };
  }, scope);
  expect(blocked).toEqual({ result: { kind: "notSent", reason: "storage" }, dispatched: false });
  // Destroy the blocked requester before releasing the old connection; it can never send later.
  await page.close();
  const retained = await holder.evaluate(
    () =>
      new Promise<RecoveryRecord[]>((resolve, reject) => {
        const db = (globalThis as unknown as { heldJournal: IDBDatabase }).heldJournal;
        const request = db.transaction("attempts", "readonly").objectStore("attempts").getAll();
        request.onerror = () => {
          reject(new Error("Read retained record failed"));
        };
        request.onsuccess = () => {
          resolve(request.result as RecoveryRecord[]);
        };
      }),
  );
  expect(retained).toEqual([earlier]);
  await holder.close();
});

test("the actual adapter closes a version-changed connection and preserves committed identity", async ({
  page,
}) => {
  await openAuthenticated(page);
  await adapter(page);
  const scope = await scopeFor(page);
  const record: RecoveryRecord = {
    ...scope,
    format: 1,
    operationId: randomUUID(),
    attemptId: randomUUID(),
    subject: "CASE",
    act: "CLOSE",
  };
  const exercise = (disableVersionHandler: boolean) =>
    page.evaluate(
      async ({ identity, suppressHandler }) => {
        const path = "/__claimcore_qa__/recovery/indexedJournal.js";
        const module = (await import(path)) as typeof IndexedJournalModule;
        const name = `claimcore-version-change-${identity.attemptId}-${String(suppressHandler)}`;
        let changedClose = false;
        let nativeVersionChanges = 0;
        let upgraded: Promise<void> = Promise.resolve();
        const factory = {
          open(databaseName: string, version: number) {
            const request = indexedDB.open(databaseName, version);
            request.addEventListener("success", () => {
              const database = request.result;
              let changing = false;
              let versionChanged = false;
              let cleanupRequested = false;
              const close = database.close.bind(database);
              let onChange: IDBDatabase["onversionchange"] = null;
              Object.defineProperty(database, "onversionchange", {
                configurable: true,
                get: () => onChange,
                set: (handler: IDBDatabase["onversionchange"]) => {
                  onChange = handler;
                },
              });
              // Keep the signal around the adapter's callback itself: browsers may run a microtask
              // checkpoint between separate listeners for the same genuine native event.
              database.addEventListener("versionchange", (event) => {
                nativeVersionChanges += 1;
                versionChanged = true;
                changing = true;
                try {
                  if (!suppressHandler) {
                    onChange?.call(database, event);
                  }
                } finally {
                  changing = false;
                  if (cleanupRequested) {
                    close();
                  }
                }
              });
              database.close = () => {
                if (changing) {
                  changedClose = true;
                  close();
                } else {
                  // Hold ordinary cleanup until the native upgrade event is delivered; otherwise
                  // a fast transaction can remove the connection before the event in some engines.
                  cleanupRequested = true;
                  if (versionChanged) {
                    close();
                  }
                }
              };
              upgraded = new Promise<void>((resolve, reject) => {
                const upgrade = indexedDB.open(name, 2);
                upgrade.onsuccess = () => {
                  upgrade.result.close();
                  resolve();
                };
                upgrade.onerror = () => {
                  reject(new Error("Upgrade positive control failed"));
                };
              });
            });
            return request;
          },
        } as IDBFactory;
        await module.createIndexedJournal(factory, name).put(identity);
        await upgraded;
        const retained = await new Promise<unknown>((resolve, reject) => {
          const request = indexedDB.open(name, 2);
          request.onsuccess = () => {
            const database = request.result;
            const tx = database.transaction("attempts", "readonly");
            const read = tx
              .objectStore("attempts")
              .get([
                identity.serviceOrigin,
                identity.installationId,
                identity.actorId,
                identity.attemptId,
              ]);
            read.onsuccess = () => {
              resolve(read.result);
            };
            tx.oncomplete = () => {
              database.close();
            };
            tx.onerror = () => {
              reject(new Error("Readback failed"));
            };
          };
          request.onerror = () => {
            reject(new Error("Readback open failed"));
          };
        });
        return { changedClose, nativeVersionChanges, retained };
      },
      { identity: record, suppressHandler: disableVersionHandler },
    );
  expect(await exercise(false)).toEqual({
    changedClose: true,
    nativeVersionChanges: 1,
    retained: record,
  });
  // Normal transaction cleanup still closes the connection when the version handler is disabled.
  // It must not satisfy the event-handler assertion.
  expect(await exercise(true)).toEqual({
    changedClose: false,
    nativeVersionChanges: 1,
    retained: record,
  });
});

test("same-identity workspace refresh settles a pending recovery without resubmission", async ({
  page,
}) => {
  await openAuthenticated(page);
  await adapter(page);
  const scope = await scopeFor(page);
  const record: RecoveryRecord = {
    ...scope,
    format: 1,
    operationId: randomUUID(),
    attemptId: randomUUID(),
    subject: "CASE",
    act: "CLOSE",
  };
  await page.evaluate(async (identity) => {
    const path = "/__claimcore_qa__/recovery/indexedJournal.js";
    const module = (await import(path)) as typeof IndexedJournalModule;
    await module.createIndexedJournal(indexedDB).put(identity);
  }, record);
  await page.reload();
  const ready = Promise.withResolvers<void>();
  const release = Promise.withResolvers<void>();
  let resolutions = 0;
  await page.route("**/api/operation.resolve", async (route) => {
    resolutions += 1;
    const response = await route.fetch({ maxRedirects: 0 });
    const value: unknown = await response.json();
    expect(isResponse("operation.resolve", value) && value.outcome.kind === "NOT_ACCEPTED").toBe(
      true,
    );
    ready.resolve();
    await release.promise;
    await route.fulfill({ response });
  });
  try {
    await page.getByRole("button", { name: "Check unresolved operation", exact: true }).click();
    await ready.promise;
    await expect(
      page.getByRole("button", { name: "Check unresolved operation", exact: true }),
    ).toBeDisabled();
    const refreshed = page.waitForResponse((response) => response.url().endsWith("/api/context"));
    await openCase(page, `RECOVERY-REFRESH-${randomUUID()}`);
    const value: unknown = await (await refreshed).json();
    if (!isResponse("context", value) || value.outcome.kind !== "SUCCEEDED") {
      throw new Error("Workspace context refresh positive control");
    }
    expect(value.outcome.data.actorId).toBe(scope.actorId);
    expect(value.outcome.data.installationId).toBe(scope.installationId);
    release.resolve();
    await expect.poll(() => records(page, scope)).toEqual([]);
    await expect(
      page.getByRole("button", { name: "Check unresolved operation", exact: true }),
    ).toHaveCount(0);
    expect(resolutions).toBe(1);
  } finally {
    release.resolve();
  }
});
