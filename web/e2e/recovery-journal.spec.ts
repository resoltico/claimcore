import { type ResponseFor, type RequestByEndpoint, taskDefinitions } from "@contracts/client";
import { freshLoginWorkflowTimeout } from "./admission-window";
import { operationId as newOperationId } from "../src/operation/operationId";
import { selectLanguage } from "./localization-support";
import { noHorizontalOverflow } from "./layout-support";
import { captureFrame, matrixEvidenceDirectory } from "./design-matrix-checks";
import { goToView } from "./workspace-navigation";
import { randomUUID } from "node:crypto";
import { readFile } from "node:fs/promises";
import { stripTypeScriptTypes } from "node:module";
import { expect, test, type Page, type Response } from "./fixtures";
import type * as IndexedJournalModule from "../src/recovery/indexedJournal";
import type * as RecoveryModule from "../src/protocol/recoveryJournal";
import type { RecoveryRecord, RecoveryScope } from "../src/protocol/recoveryJournal";
import {
  openAuthenticated,
  browserRequest,
  sessionToken,
  expectAccessible,
} from "./session-helpers";
import { startOpen, reviewDraft, openCase, startClose } from "./case-workflow";
import { isHostFailure } from "../src/protocol/client";
import { recoveryDatabaseName, recoveryRecords } from "./recovery-storage";

/** A not-started admission refusal is not the pending/final lookup observation. */
const lookupObservation = async (response: Response, operationId: string): Promise<boolean> => {
  if (!response.url().endsWith("/api/operation.get")) {
    return false;
  }
  const operationGetRequest = response
    .request()
    .postDataJSON() as RequestByEndpoint["operation.get"];
  if (operationGetRequest.operationId !== operationId) {
    return false;
  }
  if (response.status() !== 429) {
    return true;
  }
  const value: unknown = await response.json();
  return !(
    isHostFailure(value, 429) &&
    value.hostFailure.code === "RATE_LIMITED" &&
    value.hostFailure.phase === "NOT_STARTED"
  );
};
const expiredOperationId = () => {
  const prefix = (Date.now() - 11 * 60_000).toString(16).padStart(12, "0");
  return `${prefix.slice(0, 8)}-${prefix.slice(8)}${newOperationId().slice(13)}`;
};

/** Serve the actual adapter source in this isolated test page, without a product debug endpoint. */
const adapter = async (page: Page) => {
  // Bridge the actual generated declarations for the browser's native module loader.
  await page.route("**/__claimcore_qa__/task-definitions.js", (route) =>
    route.fulfill({
      contentType: "text/javascript",
      body: `export const taskDefinitions = ${JSON.stringify(taskDefinitions)};`,
    }),
  );
  for (const [path, file] of [
    ["**/__claimcore_qa__/recovery/indexedJournal.js", "../src/recovery/indexedJournal.ts"],
    ["**/__claimcore_qa__/protocol/recoveryJournal", "../src/protocol/recoveryJournal.ts"],
    ["**/__claimcore_qa__/protocol/uuid", "../src/protocol/uuid.ts"],
  ] as const) {
    const source = await readFile(new URL(file, import.meta.url), "utf8");
    const script = stripTypeScriptTypes(
      source.replace('from "@contracts/client"', 'from "/__claimcore_qa__/task-definitions.js"'),
    );
    await page.route(path, (route) =>
      route.fulfill({
        contentType: "text/javascript",
        body: script,
      }),
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
    headers: {
      "Content-Type": "application/json",
      "X-ClaimCore-Antiforgery": csrf,
    },
    body: "{}",
  });
  const contextReply = reply.payload as ResponseFor<"context", "DATA">;
  if (
    contextReply.representation !== "DATA" ||
    contextReply.outcome.kind !== "SUCCEEDED" ||
    contextReply.outcome.data.requester.kind !== "ACTIVE"
  ) {
    throw new Error("Recovery scope positive control");
  }
  return {
    serviceOrigin: new URL(page.url()).origin,
    actorId: contextReply.outcome.data.requester.actorId,
    installationId: contextReply.outcome.data.requester.installationId,
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
    format: 3,
    operationId,
    attemptId: randomUUID(),
    subject: "CASE",
    act: "case.close",
  };
  const secondRecord: RecoveryRecord = {
    ...firstRecord,
    attemptId: randomUUID(),
  };
  const put = (target: Page, record: RecoveryRecord) =>
    target.evaluate(async (inputRecord) => {
      const path = "/__claimcore_qa__/recovery/indexedJournal.js";
      const module = (await import(path)) as typeof IndexedJournalModule;
      return module.createIndexedJournal(indexedDB).put(inputRecord);
    }, record);
  expect(await Promise.all([put(page, firstRecord), put(second, secondRecord)])).toEqual([
    "inserted",
    "inserted",
  ]);
  expect(await put(second, firstRecord)).toBe("existing");
  expect((await records(page, scope)).map((record) => record.attemptId).sort()).toEqual(
    [firstRecord.attemptId, secondRecord.attemptId].sort(),
  );
  await page.evaluate(async (record) => {
    const path = "/__claimcore_qa__/recovery/indexedJournal.js";
    const module = (await import(path)) as typeof IndexedJournalModule;
    await module.createIndexedJournal(indexedDB).remove(record);
  }, firstRecord);
  expect(await records(second, scope)).toEqual([secondRecord]);
  expect(
    await records(second, {
      ...scope,
      actorId: randomUUID(),
    }),
  ).toEqual([]);
  expect(
    await records(second, {
      ...scope,
      installationId: randomUUID(),
    }),
  ).toEqual([]);
  let admissionRefusals = 0;
  await second.route("**/api/operation.get", async (route) => {
    const input = route.request().postDataJSON() as RequestByEndpoint["operation.get"];
    if (input.operationId === operationId && admissionRefusals === 0) {
      admissionRefusals += 1;
      await route.fulfill({
        status: 429,
        contentType: "application/json",
        headers: {
          "Cache-Control": "no-store",
        },
        body: JSON.stringify({
          hostFailure: {
            code: "RATE_LIMITED",
            phase: "NOT_STARTED",
          },
        }),
      });
    } else {
      await route.continue();
    }
  });
  const checked = second
    .waitForResponse((response) => lookupObservation(response, operationId))
    .then(async (response) => ({
      httpStatus: response.status(),
      value: (await response.json()) as unknown,
    }));
  await page.close();
  await second.reload();
  const { httpStatus, value: status } = await checked;
  expect(admissionRefusals).toBe(1);
  await goToView(second, "recovery");
  const operationGetReply = status as ResponseFor<"operation.get", "HUMAN">;
  if (
    operationGetReply.representation !== "HUMAN" ||
    operationGetReply.outcome.kind !== "PENDING" ||
    operationGetReply.operationId !== operationId
  ) {
    let outcome = "UNEXPECTED";
    if (operationGetReply.representation === "HUMAN") {
      outcome = operationGetReply.outcome.kind;
    } else if (isHostFailure(operationGetReply, httpStatus)) {
      outcome = operationGetReply.hostFailure.code;
    }
    throw Error(`E2E_RECOVERY_EXPECTED_PENDING_${httpStatus}_${outcome}`);
  }
  await expect(second.locator(".region-notice time")).toHaveAttribute(
    "datetime",
    operationGetReply.outcome.finalAt,
  );
  await expect(
    second.getByText("Checking whether the change was recorded. Final answer by:", {
      exact: false,
    }),
  ).toBeVisible();
  expect(await records(second, scope)).toEqual([secondRecord]);
  // A separate expired fixture exercises final absence without waiting ten minutes or changing a dispatched identity.
  await second.evaluate(async (record) => {
    const path = "/__claimcore_qa__/recovery/indexedJournal.js";
    const module = (await import(path)) as typeof IndexedJournalModule;
    await module.createIndexedJournal(indexedDB).remove(record);
  }, secondRecord);
  const expired = {
    ...secondRecord,
    operationId: expiredOperationId(),
    attemptId: randomUUID(),
  };
  const completed = second.waitForResponse((response) =>
    lookupObservation(response, expired.operationId),
  );
  await put(second, expired);
  await (await completed).json();
  await expect.poll(() => records(second, scope)).toEqual([]);
  await second.close();
});
const savedCaseAttempt = (scope: RecoveryScope): RecoveryRecord => ({
  ...scope,
  format: 3,
  operationId: expiredOperationId(),
  attemptId: randomUUID(),
  subject: "CASE",
  act: "case.close",
});
const saveAttempt = (page: Page, record: RecoveryRecord) =>
  page.evaluate(async (saved) => {
    const path = "/__claimcore_qa__/recovery/indexedJournal.js";
    const module = (await import(path)) as typeof IndexedJournalModule;
    return module.createIndexedJournal(indexedDB).put(saved);
  }, record);

test("a foreground workspace resolves the same in-flight identity while its peer is suspended @chromium @engine", async ({
  page,
  context,
}) => {
  test.setTimeout(freshLoginWorkflowTimeout);
  await openAuthenticated(page);
  await adapter(page);
  const scope = await scopeFor(page);
  const follower = await context.newPage();
  await openAuthenticated(follower);
  await adapter(follower);
  await page.bringToFront();

  const countRequests = { execute: 0, original: 0, follower: 0 };
  const initial = savedCaseAttempt(scope);
  context.on("request", (request) => {
    const { pathname } = new URL(request.url());
    if (pathname === "/api/operation.execute") {
      countRequests.execute += 1;
    }
    if (
      pathname === "/api/operation.get" &&
      (request.postDataJSON() as RequestByEndpoint["operation.get"]).operationId ===
        initial.operationId
    ) {
      const key = request.frame().page() === page ? "original" : "follower";
      countRequests[key] += 1;
    }
  });
  const lifecycle = await context.newCDPSession(page);
  await lifecycle.send("Debugger.enable");
  const arrived = Promise.withResolvers<void>();
  const completeOriginal = Promise.withResolvers<void>();
  const responseSent = Promise.withResolvers<void>();
  let intercepted = false;
  await page.route("**/api/operation.get", async (route) => {
    const request = route.request().postDataJSON() as RequestByEndpoint["operation.get"];
    if (request.operationId !== initial.operationId || intercepted) {
      await route.continue();
      return;
    }
    intercepted = true;
    arrived.resolve();
    await completeOriginal.promise;
    await route.fulfill({
      status: 429,
      headers: { "retry-after": "120" },
      json: { hostFailure: { code: "RATE_LIMITED", phase: "NOT_STARTED" } },
    });
    responseSent.resolve();
  });
  await saveAttempt(page, initial);
  await arrived.promise;
  // Pause after dispatch, even on headless Chromium where both tabs report visible.
  const suspended = new Promise<void>((resolve) => {
    lifecycle.once("Debugger.paused", () => {
      resolve();
    });
  });
  await lifecycle.send("Debugger.pause");
  await suspended;
  try {
    const received = follower.waitForResponse(
      (response) => lookupObservation(response, initial.operationId),
      { timeout: 8000 },
    );
    completeOriginal.resolve();
    await responseSent.promise;
    await follower.bringToFront();
    expect(await (await received).json()).toMatchObject({
      operationId: initial.operationId,
      outcome: { kind: "NOT_ACCEPTED" },
    });
    await expect.poll(() => records(follower, scope)).toEqual([]);
    expect(countRequests).toEqual({ execute: 0, original: 1, follower: 1 });
  } finally {
    await lifecycle.send("Debugger.resume");
    await lifecycle.detach();
  }

  await follower.close();
  await page.bringToFront();
  const afterResume = savedCaseAttempt(scope);
  const received = page.waitForResponse((response) =>
    lookupObservation(response, afterResume.operationId),
  );
  await saveAttempt(page, afterResume);
  expect(await (await received).json()).toMatchObject({ outcome: { kind: "NOT_ACCEPTED" } });
  // A long backoff from the stale response would still be visible after the old tab resumed.
  expect(
    await page.evaluate(
      (id) =>
        Object.keys(localStorage).some(
          (key) =>
            key.startsWith("claimcore-recovery-schedule:") &&
            key.includes(id) &&
            Number(localStorage.getItem(key)?.split(":")[0]) > Date.now(),
        ),
      initial.operationId,
    ),
  ).toBe(false);
  expect(countRequests.execute).toBe(0);
});

test("a writable journal supports manual checks when the recovery lease write fails @engine", async ({
  page,
}) => {
  test.setTimeout(freshLoginWorkflowTimeout);
  await page.addInitScript(() => {
    // oxlint-disable-next-line typescript/unbound-method -- Preserve the native receiver when forwarding storage writes.
    const { setItem: nativeSetItem } = Storage.prototype;
    Storage.prototype.setItem = function setItem(this: Storage, key: string, value: string) {
      if (
        this === localStorage &&
        key.startsWith("claimcore-recovery-leader:") &&
        document.documentElement?.dataset["allowRecoveryLease"] !== "true"
      ) {
        throw new DOMException("Synthetic recovery coordination quota", "QuotaExceededError");
      }
      nativeSetItem.call(this, key, value);
    };
  });
  await openAuthenticated(page);
  await adapter(page);
  const scope = await scopeFor(page);
  let executed = 0;
  const checked: string[] = [];
  page.on("request", (request) => {
    const { pathname } = new URL(request.url());
    if (pathname === "/api/operation.execute") {
      executed += 1;
    }
    if (pathname === "/api/operation.get") {
      checked.push((request.postDataJSON() as RequestByEndpoint["operation.get"]).operationId);
    }
  });

  const manual = savedCaseAttempt(scope);
  const automatic = { ...savedCaseAttempt(scope), act: "case.reopen" } as const;
  await saveAttempt(page, manual);
  await saveAttempt(page, automatic);
  const warning = page.getByRole("alert").filter({ hasText: "Automatic recovery is paused." });
  await expect(warning).toBeVisible();
  const saved = await records(page, scope);
  expect(saved).toHaveLength(2);
  expect(saved).toEqual(expect.arrayContaining([manual, automatic]));
  expect(checked).toEqual([]);
  await page.locator(".recovery-banner").getByRole("link", { name: "Check now" }).click();
  const selected = page
    .locator(".recovery-attempt")
    .filter({ has: page.getByText("Close the case", { exact: true }) })
    .getByRole("button", { name: "Check now" });
  const [manualReply] = await Promise.all([
    page.waitForResponse((response) => lookupObservation(response, manual.operationId), {
      timeout: 8000,
    }),
    selected.click({ timeout: 5000 }),
  ]);
  expect(await manualReply.json()).toMatchObject({ outcome: { kind: "NOT_ACCEPTED" } });
  await expect.poll(() => records(page, scope)).toEqual([automatic]);
  expect(checked).toEqual([manual.operationId]);
  const autoReply = page.waitForResponse((response) =>
    lookupObservation(response, automatic.operationId),
  );
  await page.locator("html").evaluate((html) => {
    html.dataset["allowRecoveryLease"] = "true";
  });
  expect(await (await autoReply).json()).toMatchObject({ outcome: { kind: "NOT_ACCEPTED" } });
  await expect.poll(() => records(page, scope)).toEqual([]);
  await expect(warning).toHaveCount(0);
  expect(checked).toEqual([manual.operationId, automatic.operationId]);
  expect(executed).toBe(0);
});

for (const failure of ["renewal", "terminal clear"] as const) {
  test(`a read permit ${failure} fault pauses automatic recovery and permits a fresh selected check @engine`, async ({
    page,
  }) => {
    test.setTimeout(freshLoginWorkflowTimeout);
    await page.addInitScript((phase) => {
      // oxlint-disable-next-line typescript/unbound-method -- Forward with the original receiver.
      const { setItem: nativeSet } = Storage.prototype;
      // oxlint-disable-next-line typescript/unbound-method -- Forward with the original receiver.
      const { getItem: nativeGet } = Storage.prototype;
      // oxlint-disable-next-line typescript/unbound-method -- Forward with the original receiver.
      const { removeItem: nativeRemove } = Storage.prototype;
      const affected = (storage: Storage, key: string) =>
        storage === localStorage &&
        key.startsWith("claimcore-recovery-schedule:") &&
        document.documentElement?.dataset["restoreReadStorage"] !== "true";
      Storage.prototype.setItem = function setItem(this: Storage, key: string, value: string) {
        if (phase === "renewal" && affected(this, key)) {
          const [, , previousOwner] = nativeGet.call(this, key)?.split(":") ?? [];
          const [, , nextOwner] = value.split(":");
          if (previousOwner !== undefined && previousOwner !== "" && previousOwner === nextOwner) {
            throw new DOMException("Synthetic permit renewal failure", "QuotaExceededError");
          }
        }
        nativeSet.call(this, key, value);
      };
      Storage.prototype.removeItem = function removeItem(this: Storage, key: string) {
        if (phase === "terminal clear" && affected(this, key)) {
          throw new DOMException("Synthetic terminal clear failure", "SecurityError");
        }
        nativeRemove.call(this, key);
      };
    }, failure);
    await openAuthenticated(page);
    await adapter(page);
    const scope = await scopeFor(page);
    const automatic = savedCaseAttempt(scope);
    const manual = { ...savedCaseAttempt(scope), act: "case.reopen" } as const;
    const counts = new Map<string, number>();
    let executed = 0;
    page.on("request", (request) => {
      if (new URL(request.url()).pathname === "/api/operation.execute") {
        executed += 1;
      }
    });
    await page.route("**/api/operation.get", async (route) => {
      const { operationId } = route.request().postDataJSON() as RequestByEndpoint["operation.get"];
      const count = (counts.get(operationId) ?? 0) + 1;
      counts.set(operationId, count);
      if (failure === "renewal" && count === 1) {
        const response = await route.fetch();
        await new Promise<void>((resolve) => {
          setTimeout(resolve, 1000);
        });
        await route.fulfill({ response });
      } else {
        await route.continue();
      }
    });

    const warning = page.getByRole("alert").filter({ hasText: "Automatic recovery is paused." });
    const terminal =
      failure === "terminal clear"
        ? page.waitForResponse((response) => lookupObservation(response, automatic.operationId))
        : null;
    await saveAttempt(page, automatic);
    if (terminal !== null) {
      expect(await (await terminal).json()).toMatchObject({ outcome: { kind: "NOT_ACCEPTED" } });
    }
    await expect(warning).toBeVisible({ timeout: 8000 });
    expect(counts.get(automatic.operationId)).toBe(1);
    expect(await records(page, scope)).toEqual([automatic]);

    // Manual recovery must survive a separate fault after acquiring its own permit.
    await saveAttempt(page, manual);
    await page.locator(".recovery-banner").getByRole("link", { name: "Check now" }).click();
    const selected = page
      .locator(".recovery-attempt")
      .filter({ has: page.getByText("Reopen the case", { exact: true }) })
      .getByRole("button", { name: "Check now" });
    await selected.click({ timeout: 8000 });
    await expect.poll(() => records(page, scope), { timeout: 8000 }).toEqual([automatic]);
    await expect(page.locator('[data-state="NOT_RECORDED"]')).toBeVisible();
    // Let two ordinary journal polling cycles pass while the fault remains active.
    await new Promise<void>((resolve) => {
      setTimeout(resolve, 2200);
    });
    await expect(warning).toBeVisible();
    expect(counts.get(manual.operationId)).toBe(2);
    expect(counts.get(automatic.operationId)).toBe(1);

    const resumed = page.waitForResponse((response) =>
      lookupObservation(response, automatic.operationId),
    );
    await page.locator("html").evaluate((html) => {
      html.dataset["restoreReadStorage"] = "true";
    });
    expect(await (await resumed).json()).toMatchObject({ outcome: { kind: "NOT_ACCEPTED" } });
    await expect.poll(() => records(page, scope)).toEqual([]);
    await expect(warning).toHaveCount(0);
    expect(counts.get(automatic.operationId)).toBe(2);
    expect(executed).toBe(0);
  });
}

test("a first explicit non-start keeps the draft and leaves no unconfirmed record @engine", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `JOURNAL-REFUSED-${randomUUID().slice(0, 8)}`;
  await startOpen(page, reference);
  const { operationId } = await reviewDraft(page);
  let intercepted = 0;
  await page.route("**/api/operation.execute", async (route) => {
    intercepted += 1;
    expect(route.request().postDataJSON()).toMatchObject({ operationId });
    const saved = await recoveryRecords(page);
    expect(saved).toHaveLength(1);
    expect(saved[0]).toMatchObject({ operationId, format: 3 });
    await route.fulfill({
      status: 429,
      json: { hostFailure: { code: "RATE_LIMITED", phase: "NOT_STARTED" } },
    });
  });
  await page.getByRole("dialog").locator("button[data-confirm-operation]").click();
  await expect(
    page.getByText("ClaimCore is busy. Try again in a moment.", { exact: true }),
  ).toBeVisible();
  await expect(page.locator('input[data-field-name="caseReference"]')).toHaveValue(reference);
  expect(intercepted).toBe(1);
  expect(await recoveryRecords(page)).toEqual([]);
  await expect(page.getByRole("button", { name: "Send again", exact: true })).toHaveCount(0);
});
test("tab loss after transaction commit and before notification leaves identity for final resolution @engine", async ({
  page,
  context,
}) => {
  await openAuthenticated(page);
  await startOpen(page, `JOURNAL-COMMIT-${randomUUID().slice(0, 8)}`);
  const identity = await reviewDraft(page);
  // Inject loss of the native completion notification after the real transaction has committed.
  await page.evaluate(() => {
    const completion = Object.getOwnPropertyDescriptor(IDBTransaction.prototype, "oncomplete");
    if (completion?.set === undefined || completion.get === undefined) {
      throw new Error("Native transaction completion setter is unavailable");
    }
    const state = globalThis as unknown as {
      journalCommitObserved: boolean;
    };
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
            {
              once: true,
            },
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
        () =>
          (
            globalThis as unknown as {
              journalCommitObserved: boolean;
            }
          ).journalCommitObserved,
      ),
    )
    .toBe(true);
  expect(executions).toBe(0);
  const replacement = await context.newPage();
  await page.close();
  const checked = replacement
    .waitForResponse((response) => lookupObservation(response, identity.operationId))
    .then((response) => response.json() as Promise<unknown>);
  await openAuthenticated(replacement);
  await adapter(replacement);
  // A document navigation could cancel bootstrap's reply before body consumption.
  const reply: unknown = await checked;
  await goToView(replacement, "recovery");
  const scope = await scopeFor(replacement);
  const saved = await records(replacement, scope);
  expect(saved).toHaveLength(1);
  expect(saved[0]?.operationId).toBe(identity.operationId);
  expect(Object.keys(saved[0]!).sort()).toEqual(keys);
  const operationGetReply = reply as ResponseFor<"operation.get", "HUMAN">;
  if (
    operationGetReply.representation !== "HUMAN" ||
    operationGetReply.outcome.kind !== "PENDING" ||
    operationGetReply.operationId !== identity.operationId
  ) {
    throw Error("Pending lookup required");
  }
  await expect(replacement.locator(".region-notice time")).toHaveAttribute(
    "datetime",
    operationGetReply.outcome.finalAt,
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
  test.setTimeout(freshLoginWorkflowTimeout);
  await openAuthenticated(page);
  await openCase(page, `JOURNAL-ACCEPTED-${randomUUID().slice(0, 8)}`);
  await adapter(page);
  const scope = await scopeFor(page);
  await startClose(page);
  const identity = await reviewDraft(page);
  await page.route("**/api/operation.get", (route) => route.abort("connectionfailed"));
  let executions = 0;
  context.on("request", (request) => {
    if (new URL(request.url()).pathname === "/api/operation.execute") {
      executions += 1;
    }
  });
  await page.route("**/api/operation.execute", async (route) => {
    const saved = await records(page, scope);
    expect(saved).toHaveLength(1);
    expect(saved[0]?.operationId).toBe(identity.operationId);
    expect(Object.keys(saved[0]!).sort()).toEqual(keys);
    const accepted = await route.fetch({
      maxRedirects: 0,
    });
    expect(accepted.status()).toBe(200);
    expect(await accepted.json()).toMatchObject({
      operationId: identity.operationId,
      outcome: { kind: "ACCEPTED" },
    });
    await route.abort("connectionfailed");
  });
  await page.getByRole("dialog").locator("button[data-confirm-operation]").click();
  await expect(
    page.getByRole("button", {
      name: "Send again",
      exact: true,
    }),
  ).toBeVisible();
  const retained = await records(page, scope);
  expect(retained).toMatchObject([
    {
      operationId: identity.operationId,
      subject: "CASE",
      act: "case.close",
    },
  ]);
  const replacement = await context.newPage();
  await replacement.emulateMedia({
    colorScheme: "light",
  });
  await page.close();
  // Inspect the committed journal before application bootstrap can resolve and remove it.
  await replacement.goto("/LICENSE.txt");
  await adapter(replacement);
  expect(await records(replacement, scope)).toEqual(retained);
  const resolved = replacement
    .waitForResponse((response) => lookupObservation(response, identity.operationId), {
      timeout: freshLoginWorkflowTimeout,
    })
    .then((response) => response.json() as Promise<unknown>);
  await openAuthenticated(replacement);
  await adapter(replacement);
  // Bootstrap checks the journal; keep its original reply alive through body consumption.
  expect(await resolved).toMatchObject({
    operationId: identity.operationId,
    outcome: { kind: "ACCEPTED", byRequester: true },
  });
  await expect.poll(() => records(replacement, scope)).toEqual([]);
  await goToView(replacement, "recovery");
  const cards = replacement.locator(".unconfirmed-list");
  await expect(cards.locator('[data-state="RECORDED"]')).toBeVisible();
  await expect(
    cards.locator('.recovery-attempt:has([data-state="RECORDED"]) .recovery-card-heading strong'),
  ).toHaveCount(0);
  await expect(
    cards.getByRole("link", {
      name: "Open case",
      exact: true,
    }),
  ).toBeVisible();
  const passive: RecoveryRecord[] = [];
  for (const operationId of [newOperationId(), expiredOperationId()]) {
    passive.push({
      ...scope,
      format: 3,
      operationId,
      attemptId: randomUUID(),
      subject: "CASE",
      act: "case.close",
    });
  }
  await replacement.evaluate(async (identities) => {
    const path = "/__claimcore_qa__/recovery/indexedJournal.js";
    const module = (await import(path)) as typeof IndexedJournalModule;
    for (const record of identities) {
      await module.createIndexedJournal(indexedDB).put(record);
    }
  }, passive);
  for (const [state, record] of [
    ["NOT_RECORDED", passive[1]!],
    ["CHECKING", passive[0]!],
  ] as const) {
    const card = cards.locator(".recovery-attempt").filter({
      has: replacement.locator(`[data-state="${state}"]`),
    });
    await expect(card).toHaveCount(1);
    await expect(card.locator(`[data-state="${state}"]`)).toBeVisible();
    await expect(card).not.toContainText(record.operationId);
    await card.getByRole("button", { name: "Technical details" }).click();
    const details = replacement.getByRole("dialog", { name: "Technical details" });
    await expect(details.getByText(record.operationId, { exact: true })).toBeVisible();
    await details.getByRole("button", { name: "Close" }).click();
    await expect(details).toBeHidden();
  }
  await expect(cards.getByText("No payment decision yet")).toBeVisible();
  await expect(cards.locator("time")).not.toHaveCount(0);
  await expect(
    cards.getByRole("button", {
      name: "Send again",
      exact: true,
    }),
  ).toHaveCount(0);
  await expectAccessible(replacement);
  await replacement.screenshot({
    path: `${await matrixEvidenceDirectory()}/recovery-recorded-en.png`,
    fullPage: true,
    animations: "disabled",
  });
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
  await replacement.setViewportSize({
    width: 320,
    height: 900,
  });
  await expect(cards.locator('[data-state="RECORDED"]')).toBeVisible();
  await noHorizontalOverflow(replacement, "narrow");
  await expectAccessible(replacement);
  await replacement.screenshot({
    path: `${await matrixEvidenceDirectory()}/recovery-recorded-ar.png`,
    fullPage: true,
    animations: "disabled",
  });
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
    format: 3,
    operationId: newOperationId(),
    attemptId: randomUUID(),
    subject: "CASE",
    act: "case.close",
  };
  await page.evaluate(async (record) => {
    const path = "/__claimcore_qa__/recovery/indexedJournal.js";
    const module = (await import(path)) as typeof IndexedJournalModule;
    await module.createIndexedJournal(indexedDB).put(record);
  }, earlier);
  const holder = await context.newPage();
  await openAuthenticated(holder);
  await holder.evaluate(
    (databaseName) =>
      new Promise<void>((resolve, reject) => {
        const request = indexedDB.open(databaseName, 1);
        request.onerror = () => {
          reject(new Error("Hold connection failed"));
        };
        request.onsuccess = () => {
          (
            globalThis as unknown as {
              heldJournal: IDBDatabase;
            }
          ).heldJournal = request.result;
          request.result.onversionchange = () => undefined;
          resolve();
        };
      }),
    recoveryDatabaseName,
  );
  const blocked = await page.evaluate(
    async ({ selectedScope, id }) => {
      const adapterPath = "/__claimcore_qa__/recovery/indexedJournal.js";
      const protocolPath = "/__claimcore_qa__/protocol/recoveryJournal";
      const journalModule = (await import(adapterPath)) as typeof IndexedJournalModule;
      const protocol = (await import(protocolPath)) as typeof RecoveryModule;
      // The injected factory requests an upgrade so the browser emits a genuine blocked event.
      const factory = {
        open: (name: string) => indexedDB.open(name, 2),
      } as IDBFactory;
      let dispatched = false;
      const record: RecoveryRecord = {
        ...selectedScope,
        format: 3,
        operationId: id,
        attemptId: crypto.randomUUID(),
        subject: "CASE",
        act: "case.open",
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
      return {
        result: prepared,
        dispatched,
      };
    },
    {
      selectedScope: scope,
      id: newOperationId(),
    },
  );
  expect(blocked).toEqual({
    result: {
      kind: "notSent",
      reason: "storage",
    },
    dispatched: false,
  });
  // Destroy the blocked requester before releasing the old connection; it can never send later.
  await page.close();
  const retained = await holder.evaluate(
    () =>
      new Promise<RecoveryRecord[]>((resolve, reject) => {
        const db = (
          globalThis as unknown as {
            heldJournal: IDBDatabase;
          }
        ).heldJournal;
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
    format: 3,
    operationId: newOperationId(),
    attemptId: randomUUID(),
    subject: "CASE",
    act: "case.close",
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
        return {
          changedClose,
          nativeVersionChanges,
          retained,
        };
      },
      {
        identity: record,
        suppressHandler: disableVersionHandler,
      },
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
    format: 3,
    operationId: expiredOperationId(),
    attemptId: randomUUID(),
    subject: "CASE",
    act: "case.close",
  };
  const ready = Promise.withResolvers<void>();
  const release = Promise.withResolvers<void>();
  let resolutions = 0;
  await page.route("**/api/operation.get", async (route) => {
    const operationGetRequest = route
      .request()
      .postDataJSON() as RequestByEndpoint["operation.get"];
    if (operationGetRequest.operationId !== record.operationId) {
      await route.continue();
      return;
    }
    resolutions += 1;
    const response = await route.fetch({
      maxRedirects: 0,
    });
    await response.json();
    ready.resolve();
    await release.promise;
    await route.fulfill({
      response,
    });
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
    await openCase(page, `RECOVERY-REFRESH-${randomUUID().slice(0, 8)}`);
    const contextReply = (await (await refreshed).json()) as ResponseFor<"context", "HUMAN">;
    if (
      contextReply.representation !== "HUMAN" ||
      contextReply.outcome.kind !== "SUCCEEDED" ||
      contextReply.outcome.document.subject.kind !== "REQUESTER" ||
      contextReply.outcome.document.subject.state !== "ACTIVE"
    ) {
      throw new Error("Workspace context refresh positive control");
    }
    expect(contextReply.outcome.document.subject.actorId).toBe(scope.actorId);
    expect(contextReply.outcome.document.subject.installationId).toBe(scope.installationId);
    release.resolve();
    await expect.poll(() => records(page, scope)).toEqual([]);
    await expect(
      page.getByRole("button", {
        name: "Check now",
        exact: true,
      }),
    ).toHaveCount(0);
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
    format: 3,
    attemptId: randomUUID(),
    operationId: newOperationId(),
    subject: "WORK",
    act: "work.update",
  };
  const reminder: RecoveryRecord = {
    ...scope,
    format: 3,
    attemptId: randomUUID(),
    operationId: newOperationId(),
    subject: "REMINDER",
    act: "reminder.completeAndPlanNext",
  };
  await page.evaluate(
    async ({ work: selectedWork, reminder: selectedReminder }) => {
      const path = "/__claimcore_qa__/recovery/indexedJournal.js";
      const module = (await import(path)) as typeof IndexedJournalModule;
      const journal = module.createIndexedJournal(indexedDB, "claimcore.qa.work-identities");
      await journal.put(selectedWork);
      await journal.put(selectedReminder);
    },
    {
      work,
      reminder,
    },
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
    async ({ record, healthy, scope: selectedScope }) => {
      const path = "/__claimcore_qa__/recovery/indexedJournal.js";
      const module = (await import(path)) as typeof IndexedJournalModule;
      const old = {
        ...record,
        format: 1,
      };
      const database = await new Promise<IDBDatabase>((resolve, reject) => {
        const request = indexedDB.open("claimcore.qa.old-identity", 1);
        request.onupgradeneeded = () => {
          request.result.createObjectStore("attempts");
          request.result.createObjectStore("confirmed");
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
        tx.objectStore("attempts").add(old, "unscopable-synthetic-key");
        for (let index = 0; index < 1000; index += 1) {
          tx.objectStore("attempts").add({ damaged: true }, [
            old.serviceOrigin,
            old.installationId,
            "00000000-0000-4000-8000-ffffffffffff",
            index.toString(),
          ]);
        }
        tx.oncomplete = () => {
          resolve();
        };
        tx.onabort = () => {
          reject(new Error(tx.error?.message ?? "IndexedDB transaction aborted"));
        };
      });
      const journal = module.createIndexedJournal(indexedDB, "claimcore.qa.old-identity");
      const faults: string[] = [];
      const publication = await journal.put(healthy);
      let visited = 0;
      const cursorDescriptor = Object.getOwnPropertyDescriptor(IDBCursor.prototype, "continue")!;
      const continuing = cursorDescriptor.value as (
        this: IDBCursor,
        position?: IDBValidKey,
      ) => void;
      Object.defineProperty(IDBCursor.prototype, "continue", {
        ...cursorDescriptor,
        value: function value(this: IDBCursor, position?: IDBValidKey) {
          visited += 1;
          continuing.call(this, position);
        },
      });
      const listed = await journal
        .list(selectedScope, (issue) => {
          faults.push(issue);
        })
        .finally(() => {
          Object.defineProperty(IDBCursor.prototype, "continue", cursorDescriptor);
        });
      let refused = false;
      try {
        await journal.put(record);
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
      return {
        refused,
        retained,
        publication,
        listed,
        faults,
        visited,
      };
    },
    {
      record: work,
      healthy: reminder,
      scope,
    },
  );
  expect(result.refused).toBe(true);
  expect(result.publication).toBe("inserted");
  expect(result.listed).toEqual([reminder]);
  expect(result.visited).toBe(2);
  expect(result.faults).toEqual(["damaged"]);
  expect(result.retained).toEqual({
    ...work,
    format: 1,
  });
});

for (const failure of ["delayed", "failed"] as const) {
  test(`healthy pending recovery progresses while confirmation discovery is ${failure} @chromium`, async ({
    page,
  }) => {
    await openAuthenticated(page);
    await adapter(page);
    const scope = await scopeFor(page);
    await page.evaluate((mode) => {
      const descriptor = Object.getOwnPropertyDescriptor(IDBDatabase.prototype, "transaction")!;
      const nativeTransaction = descriptor.value as (
        this: IDBDatabase,
        stores: string | string[],
        access?: IDBTransactionMode,
        options?: IDBTransactionOptions,
      ) => IDBTransaction;
      const completion = Object.getOwnPropertyDescriptor(IDBTransaction.prototype, "oncomplete");
      IDBDatabase.prototype.transaction = function transaction(
        this: IDBDatabase,
        stores: string | string[],
        access?: IDBTransactionMode,
        options?: IDBTransactionOptions,
      ) {
        const handoffs =
          this.name.endsWith(".4") && access === "readonly" && stores[0] === "confirmed";
        if (handoffs && mode === "failed") {
          throw new DOMException("Synthetic confirmation failure", "InvalidStateError");
        }

        const tx = nativeTransaction.call(this, stores, access, options);
        if (handoffs) {
          Object.defineProperty(tx, "oncomplete", {
            get: () => completion?.get?.call(tx) as EventListener | null,
            set: (listener: EventListener) => {
              completion?.set?.call(tx, (event: Event) => {
                window.setTimeout(() => {
                  listener.call(tx, event);
                }, 12_000);
              });
            },
          });
        }
        return tx;
      };
    }, failure);

    const pending = savedCaseAttempt(scope);
    const checked = page.waitForResponse(
      (response) => lookupObservation(response, pending.operationId),
      {
        timeout: 8_000,
      },
    );
    expect(await saveAttempt(page, pending)).toBe("inserted");
    expect(await (await checked).json()).toMatchObject({ outcome: { kind: "NOT_ACCEPTED" } });
  });
}

test("the current IndexedDB journal neither reads nor changes the previous database @engine", async ({
  page,
}) => {
  await openAuthenticated(page);
  await adapter(page);
  const scope = await scopeFor(page);
  const previous = savedCaseAttempt(scope);
  const current = savedCaseAttempt(scope);
  await page.route("**/api/operation.get", (route) => route.abort("connectionfailed"));

  const recordsBeforeReload = await page.evaluate(
    async ({ previous: oldAttempt, current: newAttempt }) => {
      const name = "claimcore.operation-recovery.3";
      const old = await new Promise<IDBDatabase>((resolve, reject) => {
        const request = indexedDB.open(name, 1);
        request.onupgradeneeded = () => {
          request.result.createObjectStore("attempts");
        };
        request.onsuccess = () => {
          resolve(request.result);
        };
        request.onerror = () => {
          reject(new Error("Cannot create the previous recovery database."));
        };
      });
      try {
        await new Promise<void>((resolve, reject) => {
          const tx = old.transaction("attempts", "readwrite");
          tx.objectStore("attempts").add(oldAttempt, [
            oldAttempt.serviceOrigin,
            oldAttempt.installationId,
            oldAttempt.actorId,
            oldAttempt.attemptId,
          ]);
          tx.oncomplete = () => {
            resolve();
          };
          tx.onabort = () => {
            reject(new Error("Cannot seed the previous recovery attempt."));
          };
        });
      } finally {
        old.close();
      }
      const path = "/__claimcore_qa__/recovery/indexedJournal.js";
      const module = (await import(path)) as typeof IndexedJournalModule;
      const journal = module.createIndexedJournal(indexedDB);
      const before = await journal.list(newAttempt);
      await journal.put(newAttempt);
      return before;
    },
    { previous, current },
  );
  expect(recordsBeforeReload).toEqual([]);

  await page.reload();
  expect(await records(page, scope)).toEqual([current]);
  const earlier = await page.evaluate(
    () =>
      new Promise<{ version: number; stores: string[]; records: RecoveryRecord[] }>(
        (resolve, reject) => {
          const request = indexedDB.open("claimcore.operation-recovery.3");
          request.onupgradeneeded = () => {
            request.transaction?.abort();
            reject(new Error("The earlier database was missing."));
          };
          request.onerror = () => {
            reject(new Error("Cannot read the previous recovery database."));
          };
          request.onsuccess = () => {
            const old = request.result;
            const tx = old.transaction("attempts", "readonly");
            const read = tx.objectStore("attempts").getAll();
            tx.oncomplete = () => {
              resolve({
                version: old.version,
                stores: Array.from(old.objectStoreNames),
                records: read.result as RecoveryRecord[],
              });
              old.close();
            };
            tx.onabort = () => {
              old.close();
              reject(new Error("Cannot verify the previous recovery attempt."));
            };
          };
        },
      ),
  );
  expect(earlier).toEqual({ version: 1, stores: ["attempts"], records: [previous] });
});

test("one-step note delivery journals first and recovers the same interrupted operation", async ({
  page,
  context,
}) => {
  test.setTimeout(freshLoginWorkflowTimeout);
  await openAuthenticated(page);
  await openCase(page, `JOURNAL-NOTE-${randomUUID().slice(0, 8)}`);
  await adapter(page);
  const scope = await scopeFor(page);
  await page.route("**/api/operation.get", (route) => route.abort("connectionfailed"));
  const operations: string[] = [];
  await page.route("**/api/operation.execute", async (route) => {
    const journal = await records(page, scope);
    expect(journal).toHaveLength(1);
    const entry = journal[0]!;
    expect(entry).toMatchObject({ act: "note.add", subject: "NOTE" });
    expect(route.request().postDataJSON()).toMatchObject({ operationId: entry.operationId });
    const response = await route.fetch({ maxRedirects: 0 });
    expect(await response.json()).toMatchObject({
      operationId: entry.operationId,
      outcome: { kind: "ACCEPTED" },
    });
    operations.push(entry.operationId);
    await route.abort("connectionfailed");
  });
  const notes = page.locator(".note-composer");
  await notes.getByRole("textbox").fill("Synthetic note with interrupted delivery");
  await notes.getByRole("button", { name: "Add note", exact: true }).click();
  await expect.poll(() => operations).toHaveLength(1);
  await expect(page.locator("button[data-confirm-operation]")).toHaveCount(0);
  const operationId = operations[0]!;
  expect(await records(page, scope)).toMatchObject([{ operationId, act: "note.add" }]);

  const replacement = await context.newPage();
  try {
    const resolved = replacement.waitForResponse((response) =>
      lookupObservation(response, operationId),
    );
    await page.close();
    await openAuthenticated(replacement);
    expect(await (await resolved).json()).toMatchObject({
      operationId,
      outcome: { kind: "ACCEPTED", byRequester: true },
    });
    await adapter(replacement);
    await expect.poll(() => records(replacement, scope)).toEqual([]);
    expect(operations).toEqual([operationId]);
  } finally {
    await replacement.close();
  }
});
