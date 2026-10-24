import { randomUUID } from "node:crypto";
import { expect, test, type Page, type Locator } from "@playwright/test";
import type {
  EndpointId,
  RequestByEndpoint,
  RecoveryItem,
  OperationReceipt,
  ResponseByEndpoint,
} from "@contracts/client";
import { isResponse } from "../src/protocol/client";
import { syntheticPrincipals } from "./authority-setup";
import { openCase, startCommand, reviewDraft } from "./case-workflow";
import {
  browserRequest,
  sessionToken,
  openAuthenticated,
  expectAccessible,
} from "./session-helpers";
import {
  pauseJsonReply,
  confirmReview,
  selectLanguage,
  noHorizontalOverflow,
  ui,
} from "./localization-support";
type Language = "en" | "lv" | "ar";
const post = async <K extends EndpointId>(page: Page, endpoint: K, input: RequestByEndpoint[K]) => {
  const response = await browserRequest(page, `/api/${endpoint}`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-ClaimCore-Antiforgery": await sessionToken(page),
    },
    body: JSON.stringify(input),
  });
  if (response.status !== 200 || !isResponse(endpoint, response.payload)) {
    throw new Error("E2E_RECOVERY_TYPED_RESPONSE_REQUIRED");
  }
  return response.payload;
};
const loseAcceptedClose = async (page: Page) => {
  const reference = `RECOVERY-${randomUUID()}`;
  await openCase(page, reference);
  await startCommand(page, "Close the case");
  await reviewDraft(page);
  await confirmReview(page);
  const pending = await pauseJsonReply(page, "operation.execute", true);
  const submitted = await (async () => {
    try {
      await page.getByRole("button", { name: ui("en", "ui.submitExact"), exact: true }).click();
      const { reply } = await pending.ready;
      if (
        reply.outcome.tag !== "ACCEPTED" ||
        reply.recoveryId === null ||
        reply.outcome.data.scope.kind !== "CASE"
      ) {
        throw new Error("E2E_RECOVERY_ACCEPTED_JOURNAL_REQUIRED");
      }
      return { reference, recoveryId: reply.recoveryId, receipt: reply.outcome.data };
    } finally {
      pending.release();
    }
  })();
  await expect(
    page.getByRole("button", { name: ui("en", "ui.retryExact"), exact: true }),
  ).toBeVisible();
  return submitted;
};
const pendingItems = async (page: Page): Promise<ReadonlyArray<RecoveryItem>> => {
  const items: RecoveryItem[] = [];
  let cursor: string | null = null;
  do {
    const reply: ResponseByEndpoint["operation.pending"] = await post(page, "operation.pending", {
      limit: 50,
      ...(cursor === null ? {} : { cursor }),
    });
    if (reply.outcome.tag !== "SUCCEEDED") {
      throw new Error("E2E_RECOVERY_PENDING_REFUSED");
    }
    items.push(...reply.outcome.data.items);
    cursor = reply.outcome.data.nextCursor;
  } while (cursor !== null);
  return items;
};
const recoveryRow = async (
  page: Page,
  operationId: string,
  language: Language,
): Promise<Locator> => {
  const section = page.locator('section[aria-labelledby="submitted-title"]');
  const row = section.getByRole("listitem").filter({ hasText: operationId });
  await expect(
    section.getByRole("button", { name: ui(language, "ui.tryAgain"), exact: true }),
  ).toBeEnabled();
  while ((await row.count()) === 0) {
    const more = section.getByRole("button", {
      name: ui(language, "ui.moreSubmitted"),
      exact: true,
    });
    if ((await more.count()) !== 1) {
      throw new Error("E2E_RECOVERY_OWNED_METADATA_MISSING");
    }
    const response = page.waitForResponse((value) =>
      value.url().endsWith("/api/operation.pending"),
    );
    await more.click();
    await response;
    await expect(
      section.getByRole("button", { name: ui(language, "ui.tryAgain"), exact: true }),
    ).toBeEnabled();
  }
  return row;
};
const reloadAndSelect = async (page: Page, operationId: string, language: Language) => {
  await page.reload();
  await page.getByRole("button", { name: ui(language, "ui.operations"), exact: true }).click();
  const pending = page.waitForResponse((value) => value.url().endsWith("/api/operation.pending"));
  await page.getByRole("button", { name: ui(language, "ui.listSubmitted"), exact: true }).click();
  await pending;
  const row = await recoveryRow(page, operationId, language);
  await expect(row).not.toContainText("Synthetic claimant");
  await row.getByRole("button", { name: ui(language, "ui.selectSubmitted"), exact: true }).click();
  await expect(
    page.getByRole("button", { name: ui(language, "ui.retryExact"), exact: true }),
  ).toBeVisible();
  await expect(
    page.getByText(ui(language, "ui.recoveryRetryDescription"), { exact: true }),
  ).toBeVisible();
};
const unchangedAcceptance = async (page: Page, reference: string, receipt: OperationReceipt) => {
  const current = await post(page, "case.get", { caseReference: reference });
  if (current.outcome.tag !== "SUCCEEDED" || receipt.scope.kind !== "CASE") {
    throw new Error("E2E_RECOVERY_CURRENT_CASE_REQUIRED");
  }
  expect(current.outcome.data.revision).toBe(receipt.scope.revision);
  const history = await post(page, "case.history", {
    caseReference: reference,
    detail: "SUMMARY",
    limit: 50,
  });
  if (history.outcome.tag !== "SUCCEEDED") {
    throw new Error("E2E_RECOVERY_HISTORY_REQUIRED");
  }
  expect(history.outcome.data.nextCursor).toBeNull();
  expect(
    history.outcome.data.entries.filter(
      (entry) => entry.tag === "SUMMARY" && entry.change.operationId === receipt.operationId,
    ),
  ).toHaveLength(1);
};
for (const language of ["en", "lv", "ar"] as const) {
  test(`submitted recovery survives browser reload, retries explicitly and acknowledges only the known result in ${language}`, async ({
    page,
  }) => {
    await openAuthenticated(page);
    const submitted = await loseAcceptedClose(page);
    await selectLanguage(page, language);
    const mutations: string[] = [];
    page.on("request", (request) => {
      const path = new URL(request.url()).pathname;
      if (path === "/api/operation.execute" || path === "/api/operation.retry") {
        mutations.push(path);
      }
    });
    await reloadAndSelect(page, submitted.receipt.operationId, language);
    expect(mutations).toEqual([]);
    await page.setViewportSize({ width: 320, height: 720 });
    await noHorizontalOverflow(page, "narrow");
    const retry = page.waitForResponse((value) => value.url().endsWith("/api/operation.retry"));
    const button = page.getByRole("button", { name: ui(language, "ui.retryExact"), exact: true });
    await button.focus();
    await page.keyboard.press("Enter");
    const response = await retry;
    expect(response.request().postDataJSON()).toEqual({
      recoveryId: submitted.recoveryId,
      operationId: submitted.receipt.operationId,
    });
    const reply: unknown = await response.json();
    if (!isResponse("operation.retry", reply) || reply.outcome.tag !== "ALREADY_ACCEPTED") {
      throw new Error("E2E_RECOVERY_EXACT_REPLAY_REQUIRED");
    }
    expect(
      JSON.stringify(reply.outcome.data) === JSON.stringify(submitted.receipt),
      "Exact acceptance and authorized disclosure are retained",
    ).toBe(true);
    await unchangedAcceptance(page, submitted.reference, submitted.receipt);
    expect(
      (await pendingItems(page)).some((item) => item.recoveryId === submitted.recoveryId),
    ).toBe(true);
    const forget = page.waitForResponse((value) => value.url().endsWith("/api/operation.forget"));
    await page
      .getByRole("button", { name: ui(language, "ui.recoveryAcknowledge"), exact: true })
      .click();
    expect((await forget).request().postDataJSON()).toEqual({ recoveryId: submitted.recoveryId });
    expect(
      (await pendingItems(page)).some((item) => item.recoveryId === submitted.recoveryId),
    ).toBe(false);
    expect(mutations).toEqual(["/api/operation.retry"]);
    await expectAccessible(page);
  });
}
test("forget from another client cannot resolve an already uncertain browser operation or cancel its acceptance", async ({
  page,
}) => {
  await openAuthenticated(page);
  const submitted = await loseAcceptedClose(page);
  await reloadAndSelect(page, submitted.receipt.operationId, "en");
  const forgotten = await post(page, "operation.forget", { recoveryId: submitted.recoveryId });
  expect(forgotten.outcome.tag).toBe("SUCCEEDED");
  await expect(
    page.getByRole("button", { name: ui("en", "ui.retryExact"), exact: true }),
  ).toBeVisible();
  const retry = page.waitForResponse((value) => value.url().endsWith("/api/operation.retry"));
  await page.getByRole("button", { name: ui("en", "ui.retryExact"), exact: true }).click();
  const reply: unknown = await (await retry).json();
  if (!isResponse("operation.retry", reply)) {
    throw new Error("E2E_RECOVERY_RETRY_RESPONSE_REQUIRED");
  }
  expect(reply.outcome.tag).toBe("REJECTED");
  await expect(
    page.getByRole("button", { name: ui("en", "ui.retryExact"), exact: true }),
  ).toBeVisible();
  await expect(
    page.getByRole("button", { name: ui("en", "ui.recoveryAcknowledge"), exact: true }),
  ).toHaveCount(0);
  await page.getByRole("button", { name: ui("en", "ui.discardAndLeave"), exact: true }).click();
  const dialog = page.getByRole("dialog");
  await expect(dialog).toContainText(ui("en", "ui.discardUnknownDescription"));
  await unchangedAcceptance(page, submitted.reference, submitted.receipt);
  await dialog.getByRole("button", { name: ui("en", "ui.keepEditing"), exact: true }).click();
  await expect(
    page.getByRole("button", { name: ui("en", "ui.retryExact"), exact: true }),
  ).toBeVisible();
});

const stageUnacceptedGrant = async (page: Page) => {
  const principals = await syntheticPrincipals();
  const reference = `RECOVERY-GRANT-${randomUUID()}`;
  const operationId = randomUUID();
  const original = await post(page, "operation.execute", {
    scope: "AUTHORITY",
    operationId,
    target: { kind: "HUMAN", issuer: principals.issuer, subject: principals.ownerSubject },
    act: {
      kind: "SET_GRANT",
      role: "CASE_READER",
      scope: { kind: "CASE", caseReference: reference },
      active: true,
    },
  });
  if (original.outcome.tag !== "REJECTED" || original.recoveryId === null) {
    throw new Error("E2E_RECOVERY_UNACCEPTED_JOURNAL_REQUIRED");
  }
  const before = await post(page, "operation.observe", { operationId });
  expect(before.outcome.tag).toBe("REJECTED");
  return { reference, operationId, recoveryId: original.recoveryId };
};
test("explicit recovery can first accept a request whose original business attempt was rejected", async ({
  page,
}) => {
  await openAuthenticated(page);
  const { reference, operationId, recoveryId } = await stageUnacceptedGrant(page);
  await openCase(page, reference);
  const mutations: string[] = [];
  page.on("request", (request) => {
    if (new URL(request.url()).pathname === "/api/operation.retry") {
      mutations.push(request.url());
    }
  });
  await reloadAndSelect(page, operationId, "en");
  expect(mutations).toEqual([]);
  const response = page.waitForResponse((value) => value.url().endsWith("/api/operation.retry"));
  await page.getByRole("button", { name: ui("en", "ui.retryExact"), exact: true }).click();
  const reply: unknown = await (await response).json();
  if (!isResponse("operation.retry", reply) || reply.outcome.tag !== "ACCEPTED") {
    throw new Error("E2E_RECOVERY_FIRST_ACCEPTANCE_REQUIRED");
  }
  expect(reply.operationId).toBe(operationId);
  expect(reply.outcome.data.scope.kind).toBe("AUTHORITY");
  const second = await post(page, "operation.retry", {
    recoveryId,
    operationId,
  });
  if (second.outcome.tag !== "ALREADY_ACCEPTED") {
    throw new Error("E2E_RECOVERY_SINGLE_ACCEPTANCE_REQUIRED");
  }
  expect(JSON.stringify(second.outcome.data) === JSON.stringify(reply.outcome.data)).toBe(true);
  const forgotten = page.waitForResponse((value) => value.url().endsWith("/api/operation.forget"));
  await page.getByRole("button", { name: ui("en", "ui.recoveryAcknowledge"), exact: true }).click();
  await forgotten;
  expect((await pendingItems(page)).some((item) => item.operationId === operationId)).toBe(false);
  expect(mutations).toHaveLength(2);
});
