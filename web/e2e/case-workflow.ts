import { businessFieldCount } from "../tests/support/inventory";
import { expect, type Page } from "@playwright/test";

import { isResponse } from "../src/protocol/client";
import type { ServiceResponse } from "../src/protocol/types";
import { progress } from "./session-helpers";

export type ReviewIdentity = Readonly<{ operationId: string }>;

const fill = async (page: Page, values: Readonly<Record<string, string>>): Promise<void> => {
  for (const [label, value] of Object.entries(values)) {
    const stage = label
      .toLowerCase()
      .replace(/[^a-z0-9]+/gu, "-")
      .slice(0, 58);
    await progress(`fill-${stage}`);
    await page.getByLabel(label, { exact: true }).fill(value);
  }
};

type SubmitOutcome = ServiceResponse<"operation.execute">["outcome"];

const submitFailureCategory = (outcome: SubmitOutcome): string => outcome.kind;

export const recordReviewed = async (page: Page): Promise<void> => {
  const confirmed = page.getByRole("checkbox", { name: "I confirm these changes.", exact: true });
  await progress("confirm-click-start");
  if ((await confirmed.count()) !== 1) {
    throw new Error("E2E_REVIEW_CHECKBOX_CARDINALITY");
  }
  try {
    await page.getByText("I confirm these changes.", { exact: true }).click({ timeout: 5_000 });
  } catch {
    throw new Error("E2E_REVIEW_LABEL_CLICK_FAILED");
  }
  await progress("confirm-clicked");
  if (!(await confirmed.isChecked())) {
    throw new Error("E2E_REVIEW_CHECKBOX_NOT_CHECKED");
  }
  await progress("confirm-checked");
  const responseEvent = page.waitForResponse(
    (response) => new URL(response.url()).pathname === "/api/operation.execute",
    { timeout: 10_000 },
  );
  await progress("submit-dispatch");
  await page.getByRole("button", { name: "Record changes" }).click();
  const response = await responseEvent;
  if (response.status() !== 200) {
    throw new Error("E2E_SUBMIT_HTTP_FAILURE");
  }
  const payload: unknown = await response.json();
  if (!isResponse("operation.execute", payload)) {
    throw new Error("E2E_SUBMIT_PROTOCOL_FAILURE");
  }
  const { outcome } = payload;
  if (outcome.kind !== "ALREADY_ACCEPTED" && outcome.kind !== "ACCEPTED") {
    throw new Error(`E2E_SUBMIT_${submitFailureCategory(outcome)}`);
  }
  await expect(page.getByRole("heading", { name: "Recorded change", exact: true })).toBeVisible();
  await expect(page.locator("section.receipt section.case-fields dl > div")).toHaveCount(
    businessFieldCount,
  );
  await progress("accepted-receipt");
};

export const startOpen = async (page: Page, caseReference: string): Promise<void> => {
  await progress("start-open");
  await page.getByRole("button", { name: "Open a case" }).click();
  await progress("open-editor");
  await fill(page, {
    "Incident date": "2026-09-01",
    "Incident notification date (FNOL)": "2026-09-02",
    "Country of incident (ISO alpha-2 code)": "LV",
    "Claimant name": "Synthetic claimant",
    "Responsible insurer": "Synthetic insurer",
    "Amount claimed": "1200.50",
    "Currency of claimed amount": "EUR",
    "Handler's case reference": caseReference,
  });
  await progress("open-filled");
};

export const startAct = async (page: Page, label: string): Promise<void> => {
  await page.getByRole("button", { name: new RegExp(`^${label}:`, "u") }).click();
};

export const reviewDraft = async (
  page: Page,
  values: Readonly<Record<string, string>> = {},
): Promise<ReviewIdentity> => {
  await fill(page, values);
  await progress("preview-dispatch");
  const event = page.waitForResponse((response) =>
    response.url().endsWith("/api/operation.preview"),
  );
  await page.getByRole("button", { name: "Review changes" }).click();
  const response = await event;
  const payload: unknown = await response.json();
  if (response.status() !== 200 || !isResponse("operation.preview", payload)) {
    throw new Error("E2E_PREVIEW_PROTOCOL_FAILURE");
  }
  const { outcome } = payload;
  if (outcome.kind !== "REVIEWED") {
    throw new Error("E2E_REVIEW_UNAVAILABLE");
  }
  const posted: unknown = response.request().postDataJSON();
  if (
    typeof posted !== "object" ||
    posted === null ||
    !("operationId" in posted) ||
    typeof posted.operationId !== "string"
  ) {
    throw new Error("Missing request identity.");
  }
  const { operationId } = posted;
  const identity = { operationId };
  const dialog = page.getByRole("dialog", { name: "Review changes" });
  await expect(dialog).toContainText(operationId);

  await progress("review-visible");
  return identity;
};

const submit = async (page: Page): Promise<void> => {
  await recordReviewed(page);
  await page.getByRole("button", { name: "Return to case" }).click();
  await progress("returned-to-case");
};

export const completeAct = async (
  page: Page,
  label: string,
  values: Readonly<Record<string, string>> = {},
): Promise<ReviewIdentity> => {
  await startAct(page, label);
  const identity = await reviewDraft(page, values);
  await submit(page);
  await expect(page.getByRole("heading", { name: "Case detail" })).toBeVisible();
  return identity;
};

export const openCase = async (page: Page, caseReference: string): Promise<ReviewIdentity> => {
  await startOpen(page, caseReference);
  const identity = await reviewDraft(page);
  await submit(page);
  await expect(page.getByRole("heading", { name: "Case detail" })).toBeVisible();
  await progress("open-detail");
  await expect(page.locator("section.case-fields").first().locator("dl > div")).toHaveCount(
    businessFieldCount,
  );
  return identity;
};
