import { openRecovery } from "./workspace-navigation";
import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "@playwright/test";
import { openCase, startAct, reviewDraft } from "./case-workflow";
import {
  openAuthenticated,
  expectAccessible,
  browserRequest,
  sessionToken,
} from "./session-helpers";
import { pauseJsonReply, confirmReview, ui } from "./localization-support";
import { recoveryRecords } from "./recovery-storage";
import { isResponseFor } from "../src/protocol/client";

const loseAcceptedClose = async (page: Page) => {
  await page.route("**/api/operation.get", (route) => route.abort("connectionfailed"));
  await openCase(page, `UNCONFIRMED-${randomUUID()}`);
  await startAct(page, "case.close", "en");
  await reviewDraft(page);
  await confirmReview(page);
  const pending = await pauseJsonReply(page, "operation.execute", true);
  try {
    await page.locator("button[data-confirm-operation]").click();
    const { reply } = await pending.ready;
    if (reply.outcome.kind !== "ACCEPTED") {
      throw new Error("Expected real accepted close");
    }
    return reply.operationId;
  } finally {
    pending.release();
  }
};
for (const reload of [false, true]) {
  test(`uncertain accepted submit recovers its receipt${reload ? " after reload" : " in page"}`, async ({
    page,
  }) => {
    await openAuthenticated(page);
    const operationId = await loseAcceptedClose(page);
    const guidance = page.getByText(ui("en", "ui.retryExactDescription"), { exact: true });
    await expect(guidance).toContainText("exactly the same change");
    await expect(guidance).toContainText("cannot be recorded twice");
    await expect(guidance).toContainText("Unconfirmed changes");
    await expect(
      page.getByRole("button", { name: ui("en", "ui.checkOutcome"), exact: true }),
    ).toBeVisible();
    const saved = await recoveryRecords(page);
    expect(saved).toHaveLength(1);
    expect(saved[0]).toMatchObject({
      format: 2,
      operationId,
      subject: "CASE",
      act: "case.close",
      serviceOrigin: new URL(page.url()).origin,
    });
    expect(Object.keys(saved[0]!).sort()).toEqual([
      "act",
      "actorId",
      "attemptId",
      "format",
      "installationId",
      "operationId",
      "serviceOrigin",
      "subject",
    ]);
    if (reload) {
      await page.reload();
      await openRecovery(page);
      await expect(
        page.getByRole("button", { name: ui("en", "ui.retryExact"), exact: true }),
      ).toHaveCount(0);
    } else {
      await expect(
        page.getByRole("button", { name: ui("en", "ui.retryExact"), exact: true }),
      ).toBeVisible();
    }
    const resolved = page.waitForResponse(
      (reply) => reply.url().endsWith("/api/operation.get") && reply.status() === 200,
    );
    await page.unroute("**/api/operation.get");
    const value: unknown = await (await resolved).json();
    expect(
      isResponseFor("operation.get", "HUMAN", value) &&
        value.outcome.kind === "ACCEPTED" &&
        value.operationId === operationId,
    ).toBe(true);
    await expect.poll(() => recoveryRecords(page)).toEqual([]);
    await expectAccessible(page);
  });
}
test("recovery storage refusal prevents handoff and storage restoration permits one send", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `STORAGE-REFUSAL-${randomUUID()}`;
  await openCase(page, reference);
  await startAct(page, "case.close", "en");
  const previewed = page.waitForRequest((request) =>
    request.url().endsWith("/api/operation.preview"),
  );
  const identity = await reviewDraft(page);
  const reviewedBytes = (await previewed).postDataBuffer();
  await confirmReview(page);
  let executions = 0;
  page.on("request", (request) => {
    if (request.url().endsWith("/api/operation.execute")) {
      executions += 1;
    }
  });
  await page.evaluate(() => {
    const open = indexedDB.open.bind(indexedDB);
    (globalThis as unknown as { restoreRecoveryStorage: () => void }).restoreRecoveryStorage =
      () => {
        indexedDB.open = open;
      };
    indexedDB.open = () => {
      throw new DOMException("Storage denied", "QuotaExceededError");
    };
  });
  await page.locator("button[data-confirm-operation]").click();
  await expect(page.getByText(ui("en", "ui.recoveryUnavailable"), { exact: true })).toBeVisible();
  expect(executions).toBe(0);
  const current = await browserRequest(page, "/api/case.get", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-ClaimCore-Antiforgery": await sessionToken(page),
    },
    body: JSON.stringify({ target: { kind: "REFERENCE", caseReference: reference } }),
  });
  if (
    !isResponseFor("case.get", "DATA", current.payload) ||
    current.payload.outcome.kind !== "SUCCEEDED" ||
    current.payload.outcome.data.state.kind !== "ACTIVE"
  ) {
    throw new Error("Storage-refusal state positive control");
  }
  expect(current.payload.outcome.data.state.fields.status).toBe("OPENED");
  await page.evaluate(() => {
    (globalThis as unknown as { restoreRecoveryStorage: () => void }).restoreRecoveryStorage();
  });
  const nextPreview = page.waitForRequest((request) =>
    request.url().endsWith("/api/operation.preview"),
  );
  const reviewedAgain = await reviewDraft(page);
  expect(reviewedAgain.operationId).not.toBe(identity.operationId);
  const nextBytes = (await nextPreview).postDataBuffer();
  if (nextBytes === null || reviewedBytes === null) {
    throw new Error("Both reviewed commands must have retained bytes");
  }
  expect(JSON.parse(nextBytes.toString())).toEqual({
    ...JSON.parse(reviewedBytes.toString()),
    operationId: reviewedAgain.operationId,
  });
  await confirmReview(page);
  const accepted = page.waitForResponse((response) =>
    response.url().endsWith("/api/operation.execute"),
  );
  await page.locator("button[data-confirm-operation]").click();
  const acceptedResponse = await accepted;
  expect(acceptedResponse.request().postDataBuffer()).toEqual(nextBytes);
  const reply: unknown = await acceptedResponse.json();
  expect(
    isResponseFor("operation.execute", "HUMAN", reply) &&
      reply.outcome.kind === "ACCEPTED" &&
      reply.operationId === reviewedAgain.operationId,
  ).toBe(true);
  expect(executions).toBe(1);
  await expect.poll(() => recoveryRecords(page)).toEqual([]);
});
