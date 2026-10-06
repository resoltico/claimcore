import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { browserRequest, openAuthenticated, sessionToken } from "./session-helpers";
import { isWebV3Response } from "../src/generated/contracts/web-v3.validation";
import type { WebV3Response } from "../src/generated/contracts/web-v3.types";
import {
  paidCase,
  chooseGroups,
  currentCase,
  history,
  expectResultFields,
} from "./localization-correction-workflow";
import {
  confirmPrepared,
  pauseJsonReply,
  preparedFrom,
  selectLanguage,
  ui,
} from "./localization-support";

for (const language of ["en", "lv", "ar"] as const) {
  test(`recovers and replays exact grouped correction bytes after unconfirmed delivery in ${language} [CC-APP-002]`, async ({
    page,
  }) => {
    const reference = `REPLAY-${randomUUID()}-A\u0308العربية`;
    const modes = { registration: "REPLACE", decision: "REPLACE", payment: "REPLACE" } as const;
    await openAuthenticated(page);
    await paidCase(page, reference);
    const before = await currentCase(page, reference);
    await chooseGroups(page, modes, language);
    const prepare = await pauseJsonReply(page, "command.prepare");
    await page.getByRole("button", { name: ui(language, "ui.prepareExact"), exact: true }).click();
    const prepared = await prepare.ready;
    const identity = preparedFrom(prepared.reply);
    prepare.release();
    await confirmPrepared(page);
    const submit = await pauseJsonReply(page, "command.execute", true);
    await page.getByRole("button", { name: ui(language, "ui.submitExact"), exact: true }).click();
    const recorded = await submit.ready;
    expect(recorded.bytes.equals(prepared.bytes)).toBe(true);
    expect(
      recorded.reply.outcome.tag === "COMPLETED" &&
        recorded.reply.outcome.data.execution.tag === "ACCEPTED",
    ).toBe(true);
    submit.release();
    await expect(page.getByRole("alert")).toBeVisible();
    await selectLanguage(page, "en");
    await page.getByRole("alert").getByRole("button", { name: "Inspect", exact: true }).click();
    const details = page.getByRole("dialog");
    await expect(details).toContainText(identity.requestSha256);
    await expect(details).not.toContainText("registration.action");
    await expect(details.locator("bdi").filter({ hasText: /^1\.0000$/u })).toHaveCount(1);
    const replay = await browserRequest(page, "/api/v3/operations/submit", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "X-ClaimCore-Antiforgery": await sessionToken(page),
      },
      body: recorded.bytes.toString("utf8"),
    });
    if (!(await isWebV3Response("command.execute", replay.payload))) {
      throw new Error("E2E_CORRECTION_REPLAY_PROTOCOL");
    }
    const payload = replay.payload as WebV3Response<"command.execute">;
    expect(payload.outcome.tag).toBe("OBSERVED_ACCEPTED");
    if (payload.outcome.tag === "OBSERVED_ACCEPTED") {
      expect(payload.outcome.data.receipt.operationId).toBe(identity.operationId);
    }
    expectResultFields(before, await currentCase(page, reference), modes);
    expect(await history(page, reference)).toHaveLength(4);
  });
}
