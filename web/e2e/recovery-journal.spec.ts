import { operationId as newOperationId } from "../src/operation/operationId";
import { selectLanguage } from "./localization-support";
import { noHorizontalOverflow } from "./layout-support";
import { captureFrame } from "./design-matrix-checks";
import { openRecovery, goToView } from "./workspace-navigation";
import { randomUUID } from "node:crypto";
import { readFile } from "node:fs/promises";
import { stripTypeScriptTypes } from "node:module";
import { expect, test, type Page } from "@playwright/test";
import type * as IndexedJournalModule from "../src/recovery/indexedJournal";
import type * as RecoveryModule from "../src/protocol/recoveryJournal";
import type { RecoveryRecord, RecoveryScope } from "../src/protocol/recoveryJournal";
import {
  openAuthenticated,
  browserRequest,
  sessionToken,
  expectAccessible,
} from "./session-helpers";
import { startOpen, reviewDraft, openCase, startAct } from "./case-workflow";
import { isResponseFor, isRequest } from "../src/protocol/client";

const expiredOperationId = () => {
  const prefix = (Date.now() - 11 * 60_000).toString(16).padStart(12, "0");
  return `${prefix.slice(0, 8)}-${prefix.slice(8)}${newOperationId().slice(13)}`;
};

/** Serve the actual adapter source in this isolated test page, without a product debug endpoint. */
const adapter = async (page: Page) => {
  const catalog = await readFile(new URL("../../contracts/catalog.json", import.meta.url), "utf8");
  // The source adapter imports actual generated metadata; storage and validation code stay intact.
  await page.route("**/__claimcore_qa__/protocol/productMetadata", (route) =>
    route.fulfill({
      contentType: "text/javascript",
      body: `export const productMetadata = {catalog:${catalog}};`,
    }),
  );
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
  // The heading can render while bootstrap reads still occupy principal admission permits.
  const list = page.locator(".worklist");
  if (await list.isVisible()) {
    await expect(list).toHaveAttribute("aria-busy", "false");
    await expect(page.locator('.summary-band[aria-busy="true"]')).toHaveCount(0);
    await expect(list.getByRole("alert")).toHaveCount(0);
  }
  const csrf = await sessionToken(page);
  const reply = await browserRequest(page, "/api/context", {
    method: "POST",
    headers: { "Content-Type": "application/json", "X-ClaimCore-Antiforgery": csrf },
    body: "{}",
  });
  if (
    !isResponseFor("context", "DATA", reply.payload) ||
    reply.payload.outcome.kind !== "SUCCEEDED" ||
    reply.payload.outcome.data.requester.kind !== "ACTIVE"
  ) {
    throw new Error("Recovery scope positive control");
  }
  return {
    serviceOrigin: new URL(page.url()).origin,
    actorId: reply.payload.outcome.data.requester.actorId,
    installationId: reply.payload.outcome.data.requester.installationId,
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

test("real IndexedDB keeps independent attempts across pages with one operation ID @engine", async ({
  page,
  context,
}) => {
  await openAuthenticated(page);
  await adapter(page);
  const scope = await scopeFor(page);
  const second = await context.newPage();
  await openAuthenticated(second);
  await adapter(second);
  const operationId = newOperationId();
  const firstRecord: RecoveryRecord = {
    ...scope,
    format: 2,
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
  const checked = second
    .waitForResponse((response) => response.url().endsWith("/api/operation.get"))
    .then((response) => response.json() as Promise<unknown>);
  await second.reload();
  await openRecovery(second);
  const status: unknown = await checked;
  if (!isResponseFor("operation.get", "HUMAN", status) || status.outcome.kind !== "PENDING") {
    throw Error("Pending lookup required");
  }
  await expect(second.locator(".region-notice time")).toHaveAttribute(
    "datetime",
    status.outcome.finalAt,
  );
  await expect(
    second.getByText("Checking whether the change was recorded. Final answer by:", {
      exact: false,
    }),
  ).toBeVisible();
  expect(await records(page, scope)).toEqual([secondRecord]);
  await page.close();
  // A separate expired fixture exercises final absence without waiting ten minutes or changing a dispatched identity.
  await second.evaluate(async (record) => {
    const path = "/__claimcore_qa__/recovery/indexedJournal.js";
    const module = (await import(path)) as typeof IndexedJournalModule;
    await module.createIndexedJournal(indexedDB).remove(record);
  }, secondRecord);
  const expired = { ...secondRecord, operationId: expiredOperationId(), attemptId: randomUUID() };
  const completed = second.waitForResponse((response) => {
    if (!response.url().endsWith("/api/operation.get")) {
      return false;
    }
    const input: unknown = response.request().postDataJSON();
    return isRequest("operation.get", input) && input.operationId === expired.operationId;
  });
  await put(second, expired);
  const final: unknown = await (await completed).json();
  expect(
    isResponseFor("operation.get", "HUMAN", final) && final.outcome.kind === "NOT_ACCEPTED",
  ).toBe(true);
  await expect.poll(() => records(second, scope)).toEqual([]);
  await second.close();
});

test("tab loss after transaction commit and before notification leaves identity for final resolution @engine", async ({
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
  await page.getByRole("dialog").locator("button[data-confirm-operation]").click();
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
  const checked = replacement
    .waitForResponse((response) => response.url().endsWith("/api/operation.get"))
    .then((response) => response.json() as Promise<unknown>);
  await openAuthenticated(replacement);
  await adapter(replacement);
  await openRecovery(replacement);
  const scope = await scopeFor(replacement);
  const saved = await records(replacement, scope);
  expect(saved).toHaveLength(1);
  expect(saved[0]?.operationId).toBe(identity.operationId);
  expect(Object.keys(saved[0]!).sort()).toEqual(keys);
  const reply: unknown = await checked;
  if (!isResponseFor("operation.get", "HUMAN", reply) || reply.outcome.kind !== "PENDING") {
    throw Error("Pending lookup required");
  }
  await expect(replacement.locator(".region-notice time")).toHaveAttribute(
    "datetime",
    reply.outcome.finalAt,
  );
  await expect(
    replacement.getByText("Checking whether the change was recorded. Final answer by:", {
      exact: false,
    }),
  ).toBeVisible();
  expect(await records(replacement, scope)).toHaveLength(1);
  expect(executions).toBe(0);
  await replacement.close();
});

test("a lost accepted reply survives tab closure and resolves without resubmission @engine", async ({
  page,
  context,
}) => {
  await openAuthenticated(page);
  await openCase(page, `JOURNAL-ACCEPTED-${randomUUID()}`);
  await adapter(page);
  const scope = await scopeFor(page);
  await startAct(page, "CLOSE", "en");
  const identity = await reviewDraft(page);
  await page.route("**/api/operation.get", (route) => route.abort("connectionfailed"));
  let executions = 0;
  await page.route("**/api/operation.execute", async (route) => {
    executions += 1;
    const saved = await records(page, scope);
    expect(saved).toHaveLength(1);
    expect(saved[0]?.operationId).toBe(identity.operationId);
    expect(Object.keys(saved[0]!).sort()).toEqual(keys);
    const accepted = await route.fetch({ maxRedirects: 0 });
    const value: unknown = await accepted.json();
    expect(
      isResponseFor("operation.execute", "HUMAN", value) && value.outcome.kind === "ACCEPTED",
    ).toBe(true);
    await route.abort("connectionfailed");
  });
  await page.getByRole("dialog").locator("button[data-confirm-operation]").click();
  await expect(page.getByRole("button", { name: "Send again", exact: true })).toBeVisible();
  const replacement = await context.newPage();
  await replacement.emulateMedia({ colorScheme: "light" });
  await page.close();
  const resolved = replacement
    .waitForResponse((response) => response.url().endsWith("/api/operation.get"))
    .then((response) => response.json() as Promise<unknown>);
  await openAuthenticated(replacement);
  await adapter(replacement);
  // Bootstrap checks the journal; keep its original reply alive through body consumption.
  const value: unknown = await resolved;
  expect(
    isResponseFor("operation.get", "HUMAN", value) &&
      value.outcome.kind === "ACCEPTED" &&
      value.operationId === identity.operationId,
  ).toBe(true);
  await expect.poll(() => records(replacement, scope)).toEqual([]);
  await goToView(replacement, "recovery");
  const cards = replacement.locator(".unconfirmed-list");
  await expect(cards.locator('[data-state="RECORDED"]')).toBeVisible();
  await expect(
    cards.locator('.recovery-attempt:has([data-state="RECORDED"]) .recovery-card-heading strong'),
  ).toHaveCount(0);
  await expect(cards.getByRole("link", { name: "Open case", exact: true })).toBeVisible();
  const passive: RecoveryRecord[] = [];
  for (const operationId of [newOperationId(), expiredOperationId()]) {
    passive.push({
      ...scope,
      format: 2,
      operationId,
      attemptId: randomUUID(),
      subject: "CASE",
      act: "CLOSE",
    });
  }
  await replacement.evaluate(async (identities) => {
    const path = "/__claimcore_qa__/recovery/indexedJournal.js";
    const module = (await import(path)) as typeof IndexedJournalModule;
    for (const record of identities) {
      await module.createIndexedJournal(indexedDB).put(record);
    }
  }, passive);
  await expect(cards.locator('[data-state="CHECKING"]')).toBeVisible();
  await expect(cards.locator('[data-state="NOT_RECORDED"]')).toBeVisible();
  await expect(cards.locator("time")).not.toHaveCount(0);
  await expect(cards.getByRole("button", { name: "Send again", exact: true })).toHaveCount(0);
  await expectAccessible(replacement);
  await captureFrame(
    replacement,
    test.info(),
    {
      width: 1280,
      language: "en",
      mode: "light",
      accent: "green",
    },
    "recovery",
  );
  await selectLanguage(replacement, "ar");
  await replacement.setViewportSize({ width: 320, height: 900 });
  await expect(cards.locator('[data-state="RECORDED"]')).toBeVisible();
  await noHorizontalOverflow(replacement, "narrow");
  await expectAccessible(replacement);
  await captureFrame(
    replacement,
    test.info(),
    {
      width: 320,
      language: "ar",
      mode: "light",
      accent: "green",
    },
    "recovery",
  );
  expect(executions).toBe(1);
  await replacement.close();
});

test("a real blocked open refuses preparation and retains the earlier record @engine", async ({
  page,
  context,
}) => {
  await openAuthenticated(page);
  await adapter(page);
  const scope = await scopeFor(page);
  const earlier: RecoveryRecord = {
    ...scope,
    format: 2,
    operationId: newOperationId(),
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
  const blocked = await page.evaluate(
    async ({ selectedScope, id }) => {
      const adapterPath = "/__claimcore_qa__/recovery/indexedJournal.js";
      const protocolPath = "/__claimcore_qa__/protocol/recoveryJournal";
      const journalModule = (await import(adapterPath)) as typeof IndexedJournalModule;
      const protocol = (await import(protocolPath)) as typeof RecoveryModule;
      // The injected factory requests an upgrade so the browser emits a genuine blocked event.
      const factory = { open: (name: string) => indexedDB.open(name, 2) } as IDBFactory;
      let dispatched = false;
      const record: RecoveryRecord = {
        ...selectedScope,
        format: 2,
        operationId: id,
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
    },
    { selectedScope: scope, id: newOperationId() },
  );
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

test("the actual adapter closes a version-changed connection and preserves committed identity @engine", async ({
  page,
}) => {
  await openAuthenticated(page);
  await adapter(page);
  const scope = await scopeFor(page);
  const record: RecoveryRecord = {
    ...scope,
    format: 2,
    operationId: newOperationId(),
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

test("same-identity workspace refresh completes an in-flight lookup without resubmission @engine", async ({
  page,
}) => {
  await openAuthenticated(page);
  await adapter(page);
  const scope = await scopeFor(page);
  const record: RecoveryRecord = {
    ...scope,
    format: 2,
    operationId: expiredOperationId(),
    attemptId: randomUUID(),
    subject: "CASE",
    act: "CLOSE",
  };
  const ready = Promise.withResolvers<void>();
  const release = Promise.withResolvers<void>();
  let resolutions = 0;
  await page.route("**/api/operation.get", async (route) => {
    resolutions += 1;
    const response = await route.fetch({ maxRedirects: 0 });
    const value: unknown = await response.json();
    expect(
      isResponseFor("operation.get", "HUMAN", value) && value.outcome.kind === "NOT_ACCEPTED",
    ).toBe(true);
    ready.resolve();
    await release.promise;
    await route.fulfill({ response });
  });
  await page.evaluate(async (identity) => {
    const path = "/__claimcore_qa__/recovery/indexedJournal.js";
    const module = (await import(path)) as typeof IndexedJournalModule;
    await module.createIndexedJournal(indexedDB).put(identity);
  }, record);
  try {
    await ready.promise;
    expect(await records(page, scope)).toEqual([record]);
    await goToView(page, "cases");
    const refreshed = page.waitForResponse((response) => response.url().endsWith("/api/context"));
    await openCase(page, `RECOVERY-REFRESH-${randomUUID()}`);
    const value: unknown = await (await refreshed).json();
    if (
      !isResponseFor("context", "HUMAN", value) ||
      value.outcome.kind !== "SUCCEEDED" ||
      value.outcome.document.context.state !== "ACTIVE"
    ) {
      throw new Error("Workspace context refresh positive control");
    }
    expect(value.outcome.document.context.actorId).toBe(scope.actorId);
    expect(value.outcome.document.context.installationId).toBe(scope.installationId);
    release.resolve();
    await expect.poll(() => records(page, scope)).toEqual([]);
    await expect(page.getByRole("button", { name: "Check now", exact: true })).toHaveCount(0);
    expect(resolutions).toBe(1);
  } finally {
    release.resolve();
  }
});

test("real IndexedDB retains only current work and reminder identities and refuses old-format records without deleting them @engine", async ({
  page,
}) => {
  await openAuthenticated(page);
  await adapter(page);
  const scope = await scopeFor(page);
  const work: RecoveryRecord = {
    ...scope,
    format: 2,
    attemptId: randomUUID(),
    operationId: newOperationId(),
    subject: "WORK",
    act: "UPDATE_WORK",
  };
  const reminder: RecoveryRecord = {
    ...scope,
    format: 2,
    attemptId: randomUUID(),
    operationId: newOperationId(),
    subject: "REMINDER",
    act: "COMPLETE_AND_PLAN_NEXT",
  };
  await page.evaluate(
    async ({ work: selectedWork, reminder: selectedReminder }) => {
      const path = "/__claimcore_qa__/recovery/indexedJournal.js";
      const module = (await import(path)) as typeof IndexedJournalModule;
      const journal = module.createIndexedJournal(indexedDB, "claimcore.qa.work-identities");
      await journal.put(selectedWork);
      await journal.put(selectedReminder);
    },
    { work, reminder },
  );
  const stored = await page.evaluate(async (selectedScope) => {
    const path = "/__claimcore_qa__/recovery/indexedJournal.js";
    const module = (await import(path)) as typeof IndexedJournalModule;
    return module
      .createIndexedJournal(indexedDB, "claimcore.qa.work-identities")
      .list(selectedScope);
  }, scope);
  expect(stored).toHaveLength(2);
  for (const record of stored) {
    expect(Object.keys(record).sort()).toEqual(keys);
  }
  const result = await page.evaluate(
    async ({ record, scope: selectedScope }) => {
      const path = "/__claimcore_qa__/recovery/indexedJournal.js";
      const module = (await import(path)) as typeof IndexedJournalModule;
      const old = { ...record, format: 1 };
      const database = await new Promise<IDBDatabase>((resolve, reject) => {
        const request = indexedDB.open("claimcore.qa.old-identity", 1);
        request.onupgradeneeded = () => {
          request.result.createObjectStore("attempts");
        };
        request.onsuccess = () => {
          resolve(request.result);
        };
        request.onerror = () => {
          reject(new Error(request.error?.message ?? "IndexedDB request failed"));
        };
      });
      const key = [old.serviceOrigin, old.installationId, old.actorId, old.attemptId];
      await new Promise<void>((resolve, reject) => {
        const tx = database.transaction("attempts", "readwrite");
        tx.objectStore("attempts").add(old, key);
        tx.oncomplete = () => {
          resolve();
        };
        tx.onabort = () => {
          reject(new Error(tx.error?.message ?? "IndexedDB transaction aborted"));
        };
      });
      let refused = false;
      try {
        await module
          .createIndexedJournal(indexedDB, "claimcore.qa.old-identity")
          .list(selectedScope);
      } catch {
        refused = true;
      }
      const retained = await new Promise<unknown>((resolve, reject) => {
        const tx = database.transaction("attempts", "readonly");
        const request = tx.objectStore("attempts").get(key);
        request.onsuccess = () => {
          resolve(request.result);
        };
        request.onerror = () => {
          reject(new Error(request.error?.message ?? "IndexedDB request failed"));
        };
      });
      database.close();
      return { refused, retained };
    },
    { record: work, scope },
  );
  expect(result.refused).toBe(true);
  expect(result.retained).toEqual({ ...work, format: 1 });
});
