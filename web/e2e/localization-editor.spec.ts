import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "@playwright/test";
import { isResponse } from "@contracts/browser-contract";
import { startOpen } from "./case-workflow";
import {
  expectAccessible,
  openAuthenticated,
  browserRequest,
  sessionToken,
} from "./session-helpers";
import {
  expectKeyboardContained,
  confirmReview,
  pauseJsonReply,
  localInputRefusalText,
  reviewFrom,
  selectFormat,
  selectLanguage,
  trackRequests,
  ui,
} from "./localization-support";

const editorLanguage = async (page: Page) => {
  for (const language of ["lv", "ar", "en"] as const) {
    await selectLanguage(page, language);
    await expect(
      page.getByRole("button", { name: ui(language, "ui.previewOperation"), exact: true }),
    ).toBeVisible();
    await expect(
      page.getByText(ui(language, "ui.unchangedUntilSubmit"), { exact: true }),
    ).toBeVisible();
  }
};
const reviewLanguage = async (page: Page) => {
  const dialog = page.getByRole("dialog");
  const confirmation = dialog.getByRole("checkbox");
  for (const language of ["en", "ar", "lv"] as const) {
    await selectLanguage(page, language);
    await expect(dialog).toHaveAccessibleName(ui(language, "ui.reviewTitle"));
    await expect(confirmation).toHaveAccessibleName(ui(language, "ui.reviewConfirmation"));
    await expect(
      dialog.getByRole("button", { name: ui(language, "ui.submitExact"), exact: true }),
    ).toBeVisible();
    await expect(
      dialog.getByRole("button", { name: ui(language, "ui.returnToEdit"), exact: true }),
    ).toBeVisible();
    await expect(
      dialog.getByText(ui(language, "ui.reviewDescription"), { exact: true }),
    ).toBeVisible();
    await expect(confirmation).toBeChecked();
  }
};

const unacceptedCase = async (page: Page, reference: string) => {
  const reply = await browserRequest(page, "/api/case.get", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "X-ClaimCore-Antiforgery": await sessionToken(page),
    },
    body: JSON.stringify({ caseReference: reference }),
  });
  if (reply.status !== 200 || !isResponse("case.get", reply.payload)) {
    throw new Error("E2E_UNACCEPTED_CASE_READ_INVALID");
  }
  expect(reply.payload.outcome.tag).toBe("REJECTED");
  if (reply.payload.outcome.tag === "REJECTED") {
    expect(reply.payload.outcome.data.diagnostic.id).toBe("ACCESS_RESOURCE_UNAVAILABLE");
  }
};
const invalidCalendar = async (page: Page, requests: string[]) => {
  await page.getByRole("button", { name: "Review changes" }).click();
  await expect(page.locator('input[name="incidentDate"]')).toBeFocused();
  await expect(page.locator('input[name="incidentDate"]')).toHaveAttribute("aria-invalid", "true");
  for (const language of ["lv", "ar", "en"] as const) {
    await selectLanguage(page, language);
    await expect(page.getByRole("alert")).toContainText(localInputRefusalText[language]);
  }
  await expect(page.locator('input[name="incidentDate"]')).toHaveValue("2026-02-30");
  await expect(page.locator('input[name="claimantName"]')).toHaveValue("A\u0308 / العربية");
  expect(requests).toHaveLength(0);
  await expect(page.locator("section.receipt")).toHaveCount(0);
  await expectAccessible(page);
};
const policyRefusal = async (page: Page, requests: string[]) => {
  const name = page.locator('input[name="claimantName"]');
  const authored = `A\u0308 / العربية${"x".repeat(201)}`;
  await page.locator('input[name="incidentDate"]').fill("2026-09-01");
  await name.fill(authored);
  const pending = await pauseJsonReply(page, "case.preview");
  try {
    await page.getByRole("button", { name: "Review changes" }).click();
    const { outcome } = (await pending.ready).reply;
    expect(outcome.tag).toBe("REJECTED");
    if (outcome.tag === "REJECTED") {
      expect(outcome.data.field).toBe("claimantName");
      expect(outcome.data.diagnostic).toEqual({
        id: "INPUT_TEXT_TOO_LONG",
        parameters: { maximumCharacters: 200 },
      });
    }
    await selectLanguage(page, "lv");
    pending.release();
    await expect(name).toBeFocused();
    await expect(name).toHaveAttribute("aria-invalid", "true");
    for (const [language, text] of [
      ["lv", "Vērtība nedrīkst pārsniegt 200 Unicode rakstzīmes."],
      ["ar", "يجب ألا تتجاوز القيمة \u2068200\u2069 محارف Unicode."],
      ["en", "The value must not exceed 200 Unicode characters."],
    ] as const) {
      await selectLanguage(page, language);
      await expect(page.getByRole("alert")).toContainText(text);
      await expect(name).toHaveAttribute("aria-invalid", "true");
    }
    await expect(name).toHaveValue(authored);
    expect(requests).toEqual(["POST /api/case.preview"]);
  } finally {
    pending.release();
  }
};

test("preserves authored Unicode and malformed calendar input, then localizes a real policy refusal", async ({
  page,
}) => {
  const reference = `LOCALE-VALIDATE-${randomUUID()}`;
  await openAuthenticated(page);
  await startOpen(page, reference);
  const name = page.locator('input[name="claimantName"]');
  await name.fill("A\u0308 / العربية");
  await page.locator('input[name="incidentDate"]').fill("2026-02-30");
  await name.evaluate((element: HTMLInputElement) => {
    element.setSelectionRange(1, 3);
  });
  const originalNode = await name.elementHandle();
  const requests = trackRequests(page);
  await editorLanguage(page);
  expect(await name.evaluate((element, prior) => element === prior, originalNode)).toBe(true);
  expect(
    await name.evaluate((element: HTMLInputElement) => [
      element.selectionStart,
      element.selectionEnd,
    ]),
  ).toEqual([1, 3]);
  expect(requests).toHaveLength(0);
  await invalidCalendar(page, requests);
  await policyRefusal(page, requests);
  await expectAccessible(page);
  await unacceptedCase(page, reference);
});

test("switches language and independent display format during draft review without changing consent", async ({
  page,
}) => {
  await openAuthenticated(page);
  await startOpen(page, `LOCALE-REVIEW-${randomUUID()}`);
  const requests = trackRequests(page);
  const pending = await pauseJsonReply(page, "case.preview");
  try {
    await page.getByRole("button", { name: "Review changes" }).click();
    const captured = await pending.ready;
    const identity = reviewFrom(captured.reply, captured.bytes);
    await selectLanguage(page, "lv");
    await selectFormat(page, "ar-EG");
    await selectLanguage(page, "ar");
    expect(requests).toHaveLength(1);
    pending.release();
    const dialog = page.getByRole("dialog");
    await expect(dialog).toBeVisible();
    const originalDialog = await dialog.elementHandle();
    const confirmation = dialog.getByRole("checkbox");
    await confirmReview(page);
    await expectKeyboardContained(page, dialog);
    await reviewLanguage(page);
    await selectFormat(page, "lv-LV");
    expect(await dialog.evaluate((element, prior) => element === prior, originalDialog)).toBe(true);
    await expect(confirmation).toBeChecked();
    await expect(dialog).toContainText(identity.operationId);
    await expect(dialog).toContainText(identity.requestSha256);
    expect(requests).toHaveLength(1);
    await expectAccessible(page);
    await selectLanguage(page, "en");
    await expect(confirmation).toBeChecked();
    await page.getByRole("button", { name: "Return to editing" }).click();
    await expect(page.getByRole("button", { name: "Review changes" })).toBeFocused();
  } finally {
    pending.release();
  }
});
