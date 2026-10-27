import { expectInputRefusal, issueMeanings } from "./input-refusal-expectations";
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
  await expectBusinessFormatRefusal(page, "claimedAmount", "DECIMAL_FORMAT", () =>
    page.getByRole("button", { name: "Review changes" }).click(),
  );
  await expectInputRefusal(page, amount, "en", {
    meaning: issueMeanings.en.claimedAmount,
    issue: businessFormatRefusalText.DECIMAL_FORMAT.en,
  });
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
    expect(outcome.data).toEqual({
      code: "INVALID_INPUT",
      action: "CORRECT_INPUT",
      issues: [{ field: "claimantName", code: "TOO_LONG", maximumCharacters: 200 }],
    });
  }
  policy.release();
  await expectInputRefusal(page, name, "en", {
    meaning: issueMeanings.en.claimantName,
    issue: "The value must not exceed 200 Unicode characters.",
  });
  await expect(name).toBeFocused();
  await expect(name).toHaveAttribute("aria-invalid", "true");
  await expect(name).toHaveAttribute("aria-describedby", /\S/u);
  await expectAccessible(page);
});
