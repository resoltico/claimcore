import { randomUUID } from "node:crypto";

import { expect, test } from "@playwright/test";

import { startOpen } from "./case-workflow";
import {
  pauseHostReply,
  pauseJsonReply,
  expectInputRefusal,
  inputRefusalText,
} from "./localization-support";
import { expectAccessible, openAuthenticated } from "./session-helpers";

test("separates malformed amount refusal from named policy-field focus", async ({ page }) => {
  await openAuthenticated(page);
  await startOpen(page, `INVALID-${randomUUID()}`);
  const amount = page.getByLabel("Amount claimed", { exact: true });
  await amount.fill("00.10");
  const malformed = await pauseHostReply(page, "case.preview", 400);
  await page.getByRole("button", { name: "Review changes" }).click();
  expectInputRefusal((await malformed.ready).reply);
  malformed.release();
  await expect(page.getByRole("alert")).toContainText(inputRefusalText.en);
  await expect(amount).toHaveValue("00.10");
  await amount.fill("0.10");
  const name = page.getByLabel("Claimant name", { exact: true });
  await name.fill("A".repeat(201));
  const policy = await pauseJsonReply(page, "case.preview");
  await page.getByRole("button", { name: "Review changes" }).click();
  const { outcome } = (await policy.ready).reply;
  expect(outcome.tag).toBe("REJECTED");
  if (outcome.tag === "REJECTED") {
    expect(outcome.data.diagnostic.id).toBe("INPUT_TEXT_TOO_LONG");
    expect(outcome.data.field).toBe("claimantName");
  }
  policy.release();
  await expect(name).toBeFocused();
  await expect(name).toHaveAttribute("aria-invalid", "true");
  await expect(name).toHaveAttribute("aria-describedby", /\S/u);
  await expectAccessible(page);
});
