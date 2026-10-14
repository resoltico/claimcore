import { expect, type Page } from "@playwright/test";

import { isResponse } from "../src/generated/contracts/validation";
import type { ServiceResponse } from "../src/api/types";
import { progress } from "./session-helpers";

export type PreparedIdentity = Readonly<{ operationId: string; requestSha256: string }>;

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

type SubmitOutcome = ServiceResponse<"command.execute">["outcome"];

const submitFailureCategory = (outcome: SubmitOutcome): string => outcome.tag;

export const recordPrepared = async (page: Page): Promise<void> => {
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
    (response) => new URL(response.url()).pathname === "/api/command.execute",
    { timeout: 10_000 },
  );
  await progress("submit-dispatch");
  await page.getByRole("button", { name: "Record changes" }).click();
  const response = await responseEvent;
  if (response.status() !== 200) {
    throw new Error("E2E_SUBMIT_HTTP_FAILURE");
  }
  const payload: unknown = await response.json();
  if (!(await isResponse("command.execute", payload))) {
    throw new Error("E2E_SUBMIT_PROTOCOL_FAILURE");
  }
  const { outcome } = payload as ServiceResponse<"command.execute">;
  if (outcome.tag !== "ALREADY_ACCEPTED" && outcome.tag !== "ACCEPTED") {
    throw new Error(`E2E_SUBMIT_${submitFailureCategory(outcome)}`);
  }
  await expect(page.getByRole("heading", { name: "Recorded change", exact: true })).toBeVisible();
  await expect(page.locator("section.receipt section.case-fields dl > div")).toHaveCount(13);
  await progress("accepted-receipt");
};

export const startOpen = async (page: Page, caseReference: string): Promise<void> => {
  await progress("start-open");
  await page.getByRole("button", { name: "Open a case" }).click();
  await progress("open-editor");
  await fill(page, {
    "Incident date": "2026-09-01",
    "Incident notification date (FNOL)": "2026-09-02",
    "Country of incident": "Latvia",
    "Claimant name": "Synthetic claimant",
    "Responsible insurer": "Synthetic insurer",
    "Amount claimed": "1200.50",
    "Currency of claimed amount": "EUR",
    "Handler's case reference": caseReference,
  });
  await progress("open-filled");
};

export const startCommand = async (page: Page, label: string): Promise<void> => {
  await page.getByRole("button", { name: new RegExp(`^${label}:`, "u") }).click();
};

export const prepare = async (
  page: Page,
  values: Readonly<Record<string, string>> = {},
): Promise<PreparedIdentity> => {
  await fill(page, values);
  await progress("prepare-dispatch");
  const event = page.waitForResponse((response) => response.url().endsWith("/api/command.preview"));
  await page.getByRole("button", { name: "Review changes" }).click();
  const response = await event;
  const payload: unknown = await response.json();
  if (response.status() !== 200 || !(await isResponse("command.preview", payload))) {
    throw new Error("E2E_PREPARE_PROTOCOL_FAILURE");
  }
  const { outcome } = payload as ServiceResponse<"command.preview">;
  if (outcome.tag !== "REVIEWED") {
    throw new Error("E2E_PREPARE_REVIEW_UNAVAILABLE");
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
  const { requestSha256 } = outcome.data;
  if (requestSha256 === null) {
    throw new Error("E2E_PREPARE_EXACT_IDENTITY_UNAVAILABLE");
  }
  const identity = { operationId, requestSha256 };
  const dialog = page.getByRole("dialog", { name: "Review changes" });
  await expect(dialog).toContainText(operationId);
  await expect(dialog).toContainText(requestSha256);
  await progress("prepare-reviewed");
  return identity;
};

const submit = async (page: Page): Promise<void> => {
  await recordPrepared(page);
  await page.getByRole("button", { name: "Return to case" }).click();
  await progress("returned-to-case");
};

export const keepForRecovery = async (page: Page): Promise<void> => {
  await page.getByRole("button", { name: "Return to editing" }).click();
  await page.getByRole("button", { name: "Back to case" }).click();
  await expect(page.getByRole("heading", { name: "Case detail" })).toBeVisible();
};

export const completeCommand = async (
  page: Page,
  label: string,
  values: Readonly<Record<string, string>> = {},
): Promise<PreparedIdentity> => {
  await startCommand(page, label);
  const identity = await prepare(page, values);
  await submit(page);
  await expect(page.getByRole("heading", { name: "Case detail" })).toBeVisible();
  return identity;
};

export const openCase = async (page: Page, caseReference: string): Promise<PreparedIdentity> => {
  await startOpen(page, caseReference);
  const identity = await prepare(page);
  await submit(page);
  await expect(page.getByRole("heading", { name: "Case detail" })).toBeVisible();
  await progress("open-detail");
  await expect(page.locator("section.case-fields").first().locator("dl > div")).toHaveCount(13);
  return identity;
};

export const droppedSubmission = async (page: Page): Promise<PreparedIdentity> => {
  await startCommand(page, "Close the case");
  const identity = await prepare(page);
  let committed = false;
  await page.route("**/api/command.execute", async (route) => {
    const response = await route.fetch();
    committed = response.status() === 200;
    await route.abort("connectionfailed");
  });
  const confirmed = page.getByRole("checkbox", { name: "I confirm these changes.", exact: true });
  await progress("drop-confirm-click-start");
  await page.getByText("I confirm these changes.", { exact: true }).click();
  await progress("drop-confirm-clicked");
  await expect(confirmed).toBeChecked();
  await progress("drop-confirm-checked");
  await page.getByRole("button", { name: "Record changes" }).click();
  await expect(page.getByRole("alert")).toContainText("unknown");
  await page.unroute("**/api/command.execute");
  expect(committed).toBe(true);
  return identity;
};
