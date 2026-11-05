import { registration } from "../tests/support/inputs";
import type { CaseFields } from "../src/api/service";
import { showExactLookup } from "./workspace-navigation";
import { fillBusinessField, fillNamedField } from "./typed-entry";
import { inventory } from "../tests/support/inventory";
import { expect, type Page, type Response } from "@playwright/test";

import { isHostFailure, isResponse } from "../src/protocol/client";
import type { ServiceResponse } from "../src/protocol/types";
import { progress } from "./session-helpers";
import { retainResponseBodies } from "./response-bodies";
import { expectCaseReadsSettled } from "./case-readiness";

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
  (["registration", "decision", "payment"] as const).reduce(
    (count, group) =>
      count +
      (fields[group] === null
        ? 0
        : inventory.groups[group].filter((field) => field.kind !== "CURRENCY").length),
    0,
  );
const responseJson = async (response: Response, phase: "SUBMIT" | "PREVIEW"): Promise<unknown> => {
  let text: string;
  try {
    text = await response.text();
  } catch {
    throw new Error(`E2E_${phase}_HTTP_${response.status()}_BODY_UNAVAILABLE`);
  }
  try {
    return JSON.parse(text) as unknown;
  } catch {
    throw new Error(`E2E_${phase}_HTTP_${response.status()}_INVALID_JSON`);
  }
};
const refuseSubmitHttp = async (response: Response): Promise<never> => {
  const status = response.status();
  const failure = await responseJson(response, "SUBMIT");
  const classification = isHostFailure(failure, status)
    ? `${failure.hostFailure.code}_${failure.hostFailure.phase ?? "NONE"}`
    : "UNCLASSIFIED_NONE";
  throw new Error(`E2E_SUBMIT_HTTP_${status}_${classification}`);
};

export const recordReviewed = async (page: Page): Promise<void> => {
  // Callers can supply an authenticated page without using openApplication's setup.
  await retainResponseBodies(page);
  const confirmed = page.getByRole("dialog").locator("button[data-confirm-operation]");
  await expect(confirmed).toHaveCount(1);
  await expect(confirmed).toBeVisible();
  await expect(confirmed).toBeEnabled();
  const responseEvent = page.waitForResponse(
    (response) => new URL(response.url()).pathname === "/api/operation.execute",
    { timeout: 10_000 },
  );
  await progress("submit-dispatch");
  await confirmed.click();
  const response = await responseEvent;
  if (response.status() !== 200) {
    await refuseSubmitHttp(response);
  }
  const payload = await responseJson(response, "SUBMIT");
  if (!isResponse("operation.execute", payload)) {
    throw new Error("E2E_SUBMIT_PROTOCOL_FAILURE");
  }
  const { outcome } = payload;
  if (outcome.kind !== "ALREADY_ACCEPTED" && outcome.kind !== "ACCEPTED") {
    throw new Error(`E2E_SUBMIT_${submitFailureCategory(outcome)}`);
  }
  await expect(page.locator("#case-detail-title")).toBeVisible();
  await expect(page.locator(".success-toast")).toBeVisible();
  const receipt = outcome.data;
  if (receipt.subject.kind !== "CASE") {
    throw new Error("Expected a case receipt.");
  }
  const { snapshot } = receipt.subject;
  const expected = snapshot.kind === "DISCLOSED" ? visibleFieldCount(snapshot.view.fields) : 0;
  await expect(page.locator("section.case-fields .field-row")).toHaveCount(expected);
  await expectCaseReadsSettled(page);
  await progress("accepted-receipt");
};

export const startOpen = async (
  page: Page,
  caseReference: string,
  title = "Open a case",
): Promise<void> => {
  await retainResponseBodies(page);
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
  await page.getByRole("link", { name: title }).click();
  await progress("open-editor");
  await page
    .getByRole("heading", { name: title, exact: true })
    .or(page.getByRole("button", { name: "Retry loading", exact: true }))
    .first()
    .waitFor({ state: "visible" });
  page.off("response", failedAsset);
  await Promise.all(bodies);
  if (await page.getByRole("button", { name: "Retry loading", exact: true }).isVisible()) {
    throw new Error(`E2E_EDITOR_LOAD_FAILURE ${failures.join("; ")}`);
  }
  const authored = registration({
    incidentDate: "2026-09-01",
    claimantName: "Synthetic claimant",
    claimedAmount: "1200.50",
  });
  for (const [name, value] of Object.entries(authored)) {
    if (value !== null) {
      await fillBusinessField(page, name, value);
    }
  }
  const reference = inventory.specialFields.find((field) => field.kind === "REFERENCE");
  if (reference === undefined) {
    throw new Error("E2E_REFERENCE_INVENTORY_MISSING");
  }
  await fillBusinessField(page, reference.key, caseReference);
  await progress("open-filled");
};

/** Reveal the offered global actions without opening an operation or sending a mutation. */
export const showCaseActionMenu = async (page: Page): Promise<void> => {
  const more = page
    .locator(".case-actions > summary, .case-more-actions > summary")
    .filter({ visible: true });
  if ((await more.count()) === 0) {
    return;
  }
  await expect(more).toHaveCount(1);
  if (
    !(await more.evaluate(
      (element) =>
        element.parentElement instanceof HTMLDetailsElement && element.parentElement.open,
    ))
  ) {
    await more.click();
  }
};

export const startAct = async (page: Page, label: string): Promise<void> => {
  const button = page.getByRole("button", { name: new RegExp(`^${label}:`, "u") });
  const more = page
    .locator(".case-actions > summary, .case-more-actions > summary")
    .filter({ visible: true });
  await button.or(more).filter({ visible: true }).first().waitFor({ state: "visible" });
  if (!(await button.isVisible())) {
    await more.click();
  }
  await button.click();
};

export const reviewDraft = async (
  page: Page,
  values: Readonly<Record<string, string>> = {},
): Promise<ReviewIdentity> => {
  await retainResponseBodies(page);
  await fill(page, values);
  await progress("preview-dispatch");
  const event = page.waitForResponse((response) =>
    response.url().endsWith("/api/operation.preview"),
  );
  await page.getByRole("button", { name: "Review changes" }).click();
  const response = await event;
  const payload = await responseJson(response, "PREVIEW");
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
  const dialog = page
    .getByRole("dialog")
    .filter({ has: page.locator("button[data-confirm-operation]") });
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
  await expect(page.locator("#case-detail-title")).toBeVisible();
  return identity;
};

export const openCase = async (page: Page, caseReference: string): Promise<ReviewIdentity> => {
  await startOpen(page, caseReference);
  const identity = await reviewDraft(page);
  await submit(page);
  await expect(page.locator("#case-detail-title")).toBeVisible();
  await progress("open-detail");
  await expect(
    page.locator("section.case-fields .case-fact-card").first().locator(".field-row"),
  ).toHaveCount(inventory.groups.registration.filter((field) => field.kind !== "CURRENCY").length);
  return identity;
};

export const lookupCase = async (page: Page, reference: string): Promise<void> => {
  const input = await showExactLookup(page);
  await input.fill(reference);
  await input.press("Enter");
};
