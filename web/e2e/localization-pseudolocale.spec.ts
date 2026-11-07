import { fillBusinessField } from "./typed-entry";
import { registration } from "../tests/support/inputs";
import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "@playwright/test";
import { openAuthenticated, expectAccessible } from "./session-helpers";
import { isResponse } from "../src/protocol/client";
import {
  seedPresentation,
  selectLanguage,
  pauseJsonReply,
  reviewFrom,
  confirmReview,
  noHorizontalOverflow,
  trackForegroundRequests,
  ui,
} from "./localization-support";

const startPseudo = async (page: Page) => {
  await seedPresentation(page, "en-XA", "ar-EG");
  await openAuthenticated(page, "⟦Cààsëës⟧");
  await page.locator("#open-case").click();
  const values = {
    caseReference: `PSEUDO-${randomUUID()}`,
    ...registration({ claimantName: "A\u0308 العربية\u200D", claimedAmount: "1.0000" }),
  };
  for (const [name, value] of Object.entries(values)) {
    if (value !== null) {
      await fillBusinessField(page, name, value);
    }
  }
  const pending = await pauseJsonReply(page, "operation.preview");
  await page.getByRole("dialog").locator('button[type="submit"]').click();
  const captured = await pending.ready;
  const identity = reviewFrom(captured.reply, captured.bytes);
  pending.release();
  await confirmReview(page);
  return { values, identity, bytes: captured.bytes };
};

test("authors and prepares in seeded pseudolocale then restores the frozen review and exact draft through Settings", async ({
  page,
}) => {
  const { values, identity, bytes } = await startPseudo(page);
  const requests = trackForegroundRequests(page);
  await page.setViewportSize({ width: 320, height: 720 });
  await noHorizontalOverflow(page, "narrow");
  await expectAccessible(page);
  await selectLanguage(page, "ar");
  // Settings parks the same operation model while the modal portal closes; restoring it creates a visible dialog node.
  const dialog = page.getByRole("dialog", { name: ui("ar", "act.OPEN.label"), exact: true });
  await expect(dialog).toBeVisible();
  await expect(dialog.locator("button[data-confirm-operation]")).toBeEnabled();
  await expect(dialog.getByRole("checkbox")).toHaveCount(0);
  await dialog.locator(".review-request > summary").click();
  await expect(dialog.locator(".review-request")).toContainText(identity.operationId);
  expect(requests).toHaveLength(0);
  await dialog.getByRole("button", { name: ui("ar", "ui.returnToEdit"), exact: true }).click();
  for (const [name, value] of Object.entries(values)) {
    if (value === null) {
      continue;
    }
    const field = dialog.locator(`[data-field-name="${name}"]`);
    const segments = field.getByRole("spinbutton");
    await expect(
      (await segments.count()) > 0 ? field.locator('input[type="hidden"]') : field,
    ).toHaveValue(value);
  }
  expect(requests).toHaveLength(0);
  // A deliberate new preview independently checks unchanged authored bytes after the route detour.
  const pending = await pauseJsonReply(page, "operation.preview");
  try {
    await dialog.getByRole("button", { name: ui("ar", "label.reviewTitle"), exact: true }).click();
    const repeated = await pending.ready;
    expect(repeated.bytes.equals(bytes)).toBe(true);
    expect(reviewFrom(repeated.reply, repeated.bytes).operationId).toBe(identity.operationId);
  } finally {
    pending.release();
  }
  expect(requests).toEqual(["POST /api/operation.preview"]);
  await page.locator('[role="dialog"] [data-open-settings]').click();
  await expect(page.locator(".settings-panel")).toBeVisible();
  await expect(page.locator('.settings-panel input[value="en-XA"]')).toHaveCount(0);
});

test("keeps expanded accepted-value layout and canonical clipboard on the normal published bytes", async ({
  page,
}) => {
  const { values, bytes } = await startPseudo(page);
  const accepted = page.waitForResponse(
    (response) => new URL(response.url()).pathname === "/api/operation.execute",
  );
  await page.getByRole("dialog").locator("button[data-confirm-operation]").click();
  const response = await accepted;
  expect(response.request().postDataBuffer()?.equals(bytes)).toBe(true);
  const reply: unknown = await response.json();
  if (
    !isResponse("operation.execute", reply) ||
    reply.outcome.kind !== "ACCEPTED" ||
    reply.outcome.data.subject.kind !== "CASE" ||
    reply.outcome.data.subject.snapshot.kind !== "DISCLOSED"
  ) {
    throw new Error("E2E_PSEUDO_ACCEPTED_CASE_MISSING");
  }
  expect(reply.outcome.data.subject.snapshot.view.fields.registration.claimedAmount).toBe("1");
  await expect(page.locator("section.case-detail")).toBeVisible();
  await expect(page.locator(".success-toast")).toBeVisible();
  await page.setViewportSize({ width: 320, height: 720 });
  await noHorizontalOverflow(page, "narrow");
  await expectAccessible(page);
  await page.setViewportSize({ width: 640, height: 900 });
  await page.evaluate(() => {
    document.documentElement.style.zoom = "2";
  });
  await noHorizontalOverflow(page, "zoom");
  await page.evaluate(() =>
    Object.defineProperty(navigator, "clipboard", {
      configurable: true,
      value: { writeText: () => Promise.reject(new Error("Synthetic clipboard denial")) },
    }),
  );
  const reference = page.locator(".case-workspace .record-view [data-entry='caseReference']");
  await reference.getByRole("button").focus();
  await reference.getByRole("button").click();
  await expect(reference.getByRole("textbox")).toHaveValue(values.caseReference);
  const toast = await page.locator(".success-toast").textContent();
  await selectLanguage(page, "en");
  await expect(page.locator(".success-toast")).toContainText("Case opened.");
  expect(toast).not.toBeNull();
  await expect(reference.getByRole("textbox")).toHaveValue(values.caseReference);
  await expect(page.locator('.record-view [data-entry="claimedAmount"] bdi').first()).toHaveText(
    "١٫٠٠ EUR",
  );
});
