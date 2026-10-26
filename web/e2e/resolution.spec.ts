import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "@playwright/test";
import { openCase, startAct, reviewDraft } from "./case-workflow";
import { openAuthenticated, expectAccessible } from "./session-helpers";
import { pauseJsonReply, confirmReview, ui } from "./localization-support";

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
    const stored = await page.evaluate(
      () =>
        JSON.parse(sessionStorage.getItem("claimcore.unresolvedOperation") ?? "null") as unknown,
    );
    expect(stored).toEqual({ operationId, subject: "CASE", act: "CLOSE" });
    if (reload) {
      await page.reload();
      await expect(
        page.getByRole("button", { name: ui("en", "ui.retryExact"), exact: true }),
      ).toHaveCount(0);
    } else {
      await expect(
        page.getByRole("button", { name: ui("en", "ui.retryExact"), exact: true }),
      ).toBeVisible();
    }
    const resolved = page.waitForResponse((reply) =>
      reply.url().endsWith("/api/operation.resolve"),
    );
    await page.getByRole("button", { name: ui("en", "ui.checkOutcome"), exact: true }).click();
    expect((await resolved).status()).toBe(200);
    await expect
      .poll(() => page.evaluate(() => sessionStorage.getItem("claimcore.unresolvedOperation")))
      .toBeNull();
    await expectAccessible(page);
  });
}
test("storage refusal leaves in-page resolution available", async ({ page }) => {
  await openAuthenticated(page);
  await page.evaluate(() => {
    Storage.prototype.setItem = () => {
      throw new Error("storage unavailable");
    };
    Storage.prototype.getItem = () => {
      throw new Error("storage unavailable");
    };
    Storage.prototype.removeItem = () => {
      throw new Error("storage unavailable");
    };
  });
  await loseAcceptedClose(page);
  const check = page.getByRole("button", { name: ui("en", "ui.checkOutcome"), exact: true });
  await expect(check).toBeVisible();
  await check.click();
  await expect(check).toHaveCount(0);
});
