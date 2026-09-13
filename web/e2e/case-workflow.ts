import { expect, type Page } from "@playwright/test";

import { isWebV2Response } from "../src/generated/convergence/web-v2.validation";
import { progress } from "./session-helpers";

export type PreparedIdentity = Readonly<{ operationId: string; requestSha256: string }>;

const identityPattern =
  /Operation\s+([0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12})\s+·\s+digest\s+([0-9a-f]{64})/u;

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

const preparedIdentity = async (page: Page): Promise<PreparedIdentity> => {
  const dialog = page.getByRole("dialog", { name: "Review prepared operation" });
  await expect(dialog).toBeVisible();
  const text = (await dialog.textContent()) ?? "";
  const match = identityPattern.exec(text);
  const operationId = match?.[1];
  const requestSha256 = match?.[2];
  if (operationId === undefined || requestSha256 === undefined) {
    throw new Error("Prepared operation identity was not rendered.");
  }
  return { operationId, requestSha256 };
};

const submitReview = async (page: Page): Promise<void> => {
  const confirmed = page.getByRole("checkbox", { name: /submit this exact prepared request/u });
  await progress("confirm-click-start");
  if ((await confirmed.count()) !== 1) throw new Error("E2E_REVIEW_CHECKBOX_CARDINALITY");
  try {
    await page
      .getByText("I will submit this exact prepared request.", { exact: true })
      .click({ timeout: 5_000 });
  } catch {
    throw new Error("E2E_REVIEW_LABEL_CLICK_FAILED");
  }
  await progress("confirm-clicked");
  if (!(await confirmed.isChecked())) throw new Error("E2E_REVIEW_CHECKBOX_NOT_CHECKED");
  await progress("confirm-checked");
  const responseEvent = page.waitForResponse(
    (response) => new URL(response.url()).pathname === "/api/v2/operations/submit",
    { timeout: 10_000 },
  );
  await progress("submit-dispatch");
  await page.getByRole("button", { name: "Submit exact request" }).click();
  const response = await responseEvent;
  if (response.status() !== 200) throw new Error("E2E_SUBMIT_HTTP_FAILURE");
  const payload: unknown = await response.json();
  if (!isWebV2Response("command.execute", payload)) throw new Error("E2E_SUBMIT_PROTOCOL_FAILURE");
  const outcome = payload.outcome;
  if (
    outcome.tag !== "OBSERVED_ACCEPTED" &&
    !(outcome.tag === "COMPLETED" && outcome.data.execution.tag === "ACCEPTED")
  )
    throw new Error("E2E_SUBMIT_BUSINESS_FAILURE");
  await expect(
    page.getByRole("heading", { name: "Accepted operation", exact: true }),
  ).toBeVisible();
  await expect(page.locator("section.receipt section.case-fields dl > div")).toHaveCount(13);
  await progress("accepted-receipt");
};

export const startOpen = async (page: Page, caseReference: string): Promise<void> => {
  await progress("start-open");
  await page.getByRole("button", { name: "Open new case" }).click();
  await progress("open-editor");
  await fill(page, {
    "Incident date": "2026-09-01",
    "Incident notification date (FNOL)": "2026-09-02",
    "Country of incident": "Latvia",
    "Claimant name": "Synthetic claimant",
    "Allegedly responsible insurer": "Synthetic insurer",
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
  await page.getByRole("button", { name: "Prepare exact request" }).click();
  const identity = await preparedIdentity(page);
  await progress("prepare-reviewed");
  return identity;
};

const submit = async (page: Page): Promise<void> => {
  await submitReview(page);
  await page.getByRole("button", { name: "Return to case" }).click();
  await progress("returned-to-case");
};

export const keepForRecovery = async (page: Page): Promise<void> => {
  await page.getByRole("button", { name: "Keep for Recovery" }).click();
  await page.getByRole("button", { name: "Back without preparing" }).click();
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
  await page.getByRole("button", { name: caseReference }).click();
  await progress("open-detail");
  await expect(page.getByRole("heading", { name: "Case detail" })).toBeVisible();
  await expect(page.locator("section.case-fields").first().locator("dl > div")).toHaveCount(13);
  return identity;
};

export const droppedSubmission = async (page: Page): Promise<PreparedIdentity> => {
  await startCommand(page, "Close the case");
  const identity = await prepare(page);
  let committed = false;
  await page.route("**/api/v2/operations/submit", async (route) => {
    const response = await route.fetch();
    committed = response.status() === 200;
    await route.abort("connectionfailed");
  });
  const confirmed = page.getByRole("checkbox", { name: /submit this exact prepared request/u });
  await progress("drop-confirm-click-start");
  await page.getByText("I will submit this exact prepared request.", { exact: true }).click();
  await progress("drop-confirm-clicked");
  await expect(confirmed).toBeChecked();
  await progress("drop-confirm-checked");
  await page.getByRole("button", { name: "Submit exact request" }).click();
  await expect(page.getByRole("alert")).toContainText("Recovery");
  await page.unroute("**/api/v2/operations/submit");
  expect(committed).toBe(true);
  return identity;
};
