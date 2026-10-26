import { randomUUID } from "node:crypto";

import { expect, test } from "@playwright/test";

import { startOpen } from "./case-workflow";
import {
  pauseJsonReply,
  businessFormatRefusalText,
  expectBusinessFormatRefusal,
  trackRequests,
} from "./localization-support";
import { expectAccessible, openAuthenticated } from "./session-helpers";

test("focuses named fields for malformed amount and subsequent policy refusal", async ({
  page,
}) => {
  await openAuthenticated(page);
  await startOpen(page, `INVALID-${randomUUID()}`);
  const amount = page.getByLabel("Amount claimed", { exact: true });
  await amount.fill("00.10");
  const requests = trackRequests(page);
  await expectBusinessFormatRefusal(page, "claimedAmount", "INPUT_DECIMAL_FORMAT", () =>
    page.getByRole("button", { name: "Review changes" }).click(),
  );
  await expect(page.getByRole("alert")).toContainText(
    businessFormatRefusalText.INPUT_DECIMAL_FORMAT.en,
  );
  await expect(amount).toBeFocused();
  await expect(amount).toHaveAttribute("aria-invalid", "true");
  expect(requests).toEqual(["POST /api/operation.preview"]);
  await expect(amount).toHaveValue("00.10");
  await amount.fill("0.10");
  const name = page.getByLabel("Claimant name", { exact: true });
  await name.fill("A".repeat(201));
  const policy = await pauseJsonReply(page, "operation.preview");
  await page.getByRole("button", { name: "Review changes" }).click();
  const { outcome } = (await policy.ready).reply;
  expect(outcome.kind).toBe("REJECTED");
  if (outcome.kind === "REJECTED") {
    expect(outcome.data.code).toBe("INPUT_TEXT_TOO_LONG");
    expect(outcome.data.target).toBe("claimantName");
  }
  policy.release();
  await expect(name).toBeFocused();
  await expect(name).toHaveAttribute("aria-invalid", "true");
  await expect(name).toHaveAttribute("aria-describedby", /\S/u);
  await expectAccessible(page);
});
