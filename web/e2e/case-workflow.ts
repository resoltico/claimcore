import type { CaseFields } from "../src/api/service";
import { showExactLookup } from "./workspace-navigation";
import { fillNamedField } from "./typed-entry";
import { businessFieldCount, inventory } from "../tests/support/inventory";
import { expect, type Page, type Response } from "@playwright/test";

import { isHostFailure, isResponse } from "../src/protocol/client";
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
    await fillNamedField(page, label, value);
  }
};

type SubmitOutcome = ServiceResponse<"operation.execute">["outcome"];

const submitFailureCategory = (outcome: SubmitOutcome): string => outcome.kind;

const visibleFieldCount = (fields: CaseFields): number =>
  businessFieldCount -
  inventory.workFields.length -
  (fields.decision === null ? inventory.groups.decision.length : 0) -
  (fields.payment === null ? inventory.groups.payment.length : 0);
const refuseSubmitHttp = async (response: Response): Promise<never> => {
  const status = response.status();
  const failure: unknown = await response.json().catch(() => null);
  const classification = isHostFailure(failure, status)
    ? `${failure.hostFailure.code}_${failure.hostFailure.phase ?? "NONE"}`
    : "UNCLASSIFIED_NONE";
  throw new Error(`E2E_SUBMIT_HTTP_${status}_${classification}`);
};

export const recordReviewed = async (
  page: Page,
  confirmation = "I confirm these changes.",
): Promise<void> => {
  const confirmed = page.getByRole("checkbox", { name: confirmation, exact: true });
  await confirmed.waitFor({ state: "visible" });
  await progress("confirm-click-start");
  if ((await confirmed.count()) !== 1) {
    throw new Error("E2E_REVIEW_CHECKBOX_CARDINALITY");
  }
  try {
    await page.getByText(confirmation, { exact: true }).click({ timeout: 5_000 });
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
    await refuseSubmitHttp(response);
  }
  const payload: unknown = await response.json();
  if (!isResponse("operation.execute", payload)) {
    throw new Error("E2E_SUBMIT_PROTOCOL_FAILURE");
  }
  const { outcome } = payload;
  if (outcome.kind !== "ALREADY_ACCEPTED" && outcome.kind !== "ACCEPTED") {
    throw new Error(`E2E_SUBMIT_${submitFailureCategory(outcome)}`);
  }
  await expect(page.getByRole("heading", { name: "Case detail", exact: true })).toBeVisible();
  await expect(page.locator(".success-toast")).toContainText("revision");
  const receipt = outcome.data;
  if (receipt.subject.kind !== "CASE") {
    throw new Error("Expected a case receipt.");
  }
  const { snapshot } = receipt.subject;
  const expected = snapshot.kind === "DISCLOSED" ? visibleFieldCount(snapshot.view.fields) : 0;
  await expect(page.locator("section.case-fields dl > div")).toHaveCount(expected);
  await progress("accepted-receipt");
};

export const startOpen = async (page: Page, caseReference: string): Promise<void> => {
  await progress("start-open");
  const failures: string[] = [];
  const bodies: Promise<void>[] = [];
  const failedAsset = (response: Response) => {
    const path = new URL(response.url()).pathname;
    if (path.startsWith("/assets/") && response.status() >= 400) {
      failures.push(`${path}: ${response.status()}, ${response.headers()["content-type"]}`);
      bodies.push(
        response
          .text()
          .then((body) => {
            failures.push(body.slice(0, 512));
          })
          .catch(() => {
            failures.push("Response body unavailable");
          }),
      );
    }
  };
  page.on("response", failedAsset);
  await page.getByRole("button", { name: "Open a case" }).click();
  await progress("open-editor");
  await page
    .getByRole("heading", { name: "Open a case", exact: true })
    .or(page.getByRole("button", { name: "Retry loading", exact: true }))
    .first()
    .waitFor({ state: "visible" });
  page.off("response", failedAsset);
  await Promise.all(bodies);
  if (await page.getByRole("button", { name: "Retry loading", exact: true }).isVisible()) {
    throw new Error(`E2E_EDITOR_LOAD_FAILURE ${failures.join("; ")}`);
  }
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
  const button = page.getByRole("button", { name: new RegExp(`^${label}:`, "u") });
  const more = page.locator(".case-more-actions > summary");
  await button.or(more).first().waitFor({ state: "visible" });
  if (!(await button.isVisible())) {
    await more.click();
  }
  await button.click();
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
    inventory.specialFields.length + inventory.groups.registration.length,
  );
  return identity;
};

export const lookupCase = async (page: Page, reference: string): Promise<void> => {
  const input = await showExactLookup(page);
  await input.fill(reference);
  await input.press("Enter");
};
