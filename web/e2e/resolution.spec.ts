import { goToView } from "./workspace-navigation";
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
import { isResponse } from "../src/protocol/client";

const loseAcceptedClose = async (page: Page) => {
  await openCase(page, `RESOLVE-${randomUUID()}`);
  await startAct(page, "Close the case");
  await reviewDraft(page);
  await confirmReview(page);
  const pending = await pauseJsonReply(page, "operation.execute", true);
  try {
    await page.getByRole("button", { name: ui("en", "ui.submitExact"), exact: true }).click();
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
  test(`uncertain submit has definitive resolution${reload ? " after reload" : " in page"}`, async ({
    page,
  }) => {
    await openAuthenticated(page);
    const operationId = await loseAcceptedClose(page);
    await expect(
      page.getByRole("button", { name: ui("en", "ui.checkOutcome"), exact: true }),
    ).toBeVisible();
    const saved = await recoveryRecords(page);
    expect(saved).toHaveLength(1);
    expect(saved[0]).toMatchObject({
      format: 1,
      operationId,
      subject: "CASE",
      act: "CLOSE",
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
      await goToView(page, "operations");
      await expect(
        page.getByRole("button", { name: ui("en", "ui.retryExact"), exact: true }),
      ).toHaveCount(0);
    } else {
      await expect(
        page.getByRole("button", { name: ui("en", "ui.retryExact"), exact: true }),
      ).toBeVisible();
    }
    const resolved = page.waitForResponse((reply) => reply.url().endsWith("/api/operation.status"));
    const check = reload
      ? page
          .getByRole("status")
          .filter({ hasText: operationId })
          .getByRole("button", { name: ui("en", "ui.checkUnresolvedOperation"), exact: true })
      : page.getByRole("button", { name: ui("en", "ui.checkOutcome"), exact: true });
    await check.click();
    expect((await resolved).status()).toBe(200);
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
  await startAct(page, "Close the case");
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
  await page.getByRole("button", { name: ui("en", "ui.submitExact"), exact: true }).click();
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
    !isResponse("case.get", current.payload) ||
    current.payload.outcome.kind !== "SUCCEEDED" ||
    current.payload.outcome.data.state.kind !== "ACTIVE"
  ) {
    throw new Error("Storage-refusal state positive control");
  }
  expect(current.payload.outcome.data.state.fields.status).toBe("OPENED");
  await page.evaluate(() => {
    (globalThis as unknown as { restoreRecoveryStorage: () => void }).restoreRecoveryStorage();
  });
  const reviewedAgain = await reviewDraft(page);
  expect(reviewedAgain.operationId).toBe(identity.operationId);
  await confirmReview(page);
  const accepted = page.waitForResponse((response) =>
    response.url().endsWith("/api/operation.execute"),
  );
  await page.getByRole("button", { name: ui("en", "ui.submitExact"), exact: true }).click();
  const acceptedResponse = await accepted;
  expect(acceptedResponse.request().postDataBuffer()).toEqual(reviewedBytes);
  const reply: unknown = await acceptedResponse.json();
  expect(
    isResponse("operation.execute", reply) &&
      reply.outcome.kind === "ACCEPTED" &&
      reply.operationId === identity.operationId,
  ).toBe(true);
  expect(executions).toBe(1);
  await expect.poll(() => recoveryRecords(page)).toEqual([]);
});
