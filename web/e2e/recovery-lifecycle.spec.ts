import { randomUUID } from "node:crypto";

import { expect, test, type Page } from "@playwright/test";

import { isWebV2Response } from "../src/generated/convergence/web-v2.validation";
import type { WebV2Response } from "../src/generated/convergence/web-v2.types";
import {
  droppedSubmission,
  keepForRecovery,
  openCase,
  prepare,
  startCommand,
  startOpen,
  type PreparedIdentity,
} from "./case-workflow";
import { exportEnvelope, previewAndRetain } from "./recovery-artifacts";
import {
  browserRequest,
  expectAccessible,
  openAuthenticated,
  progress,
  sessionToken,
} from "./session-helpers";

const recoveryRow = (page: Page, identity: PreparedIdentity) =>
  page.locator(".recovery-list li").filter({ hasText: identity.operationId });

const inspect = async (page: Page, identity: PreparedIdentity): Promise<void> => {
  await progress("recovery-inspect");
  const row = recoveryRow(page, identity);
  try {
    await expect(row).toHaveCount(1);
  } catch {
    throw new Error(
      (await row.count()) === 0 ? "E2E_RECOVERY_ROW_MISSING" : "E2E_RECOVERY_ROW_DUPLICATED",
    );
  }
  await progress("recovery-row-found");
  if (!(await row.isVisible())) throw new Error("E2E_RECOVERY_ROW_HIDDEN");
  await row.getByRole("button", { name: "Inspect" }).click();
  await progress("recovery-dialog-opened");
  const dialog = page.getByRole("dialog", { name: "Recovery details" });
  await expect(dialog).toContainText(identity.operationId);
  await expect(dialog).toContainText("Expected revision");
  await progress("recovery-detail-rendered");
  await expectAccessible(page);
};

const navigateRecovery = async (page: Page): Promise<void> => {
  await progress("recovery-navigation");
  await expect(page.locator("main.app-shell header small")).toBeVisible();
  const navigation = page.getByRole("button", { name: "Recovery", exact: true });
  await expect(navigation).toBeEnabled();
  await navigation.click();
  await expect(navigation).toHaveAttribute("aria-pressed", "true");
  await expect(page.getByRole("heading", { name: "Recovery", exact: true })).toBeVisible();
  await expectAccessible(page);
};

const selectRecoveryView = async (page: Page, view: "PENDING" | "TERMINAL"): Promise<void> => {
  const selector = page.getByLabel("Recovery view");
  await selector.selectOption(view);
  await expect(selector).toHaveValue(view);
};

const preparedDecision = async (page: Page, caseReference: string): Promise<PreparedIdentity> => {
  await openCase(page, caseReference);
  await startCommand(page, "Record payment decision");
  const identity = await prepare(page, {
    "Payment decision date": "2026-09-03",
    "Amount to be paid": "300.25",
    "Currency of amount to be paid": "EUR",
  });
  await keepForRecovery(page);
  await navigateRecovery(page);
  return identity;
};

const resolveAndObserve = async (page: Page, identity: PreparedIdentity): Promise<void> => {
  await inspect(page, identity);
  await page.getByRole("button", { name: "Resolve exact preparation" }).click();
  const resolve = page.getByRole("dialog", { name: "Resolve exact preparation?" });
  await expect(resolve).toContainText(identity.requestSha256);
  await expectAccessible(page);
  await resolve.getByRole("button", { name: "Confirm resolve" }).click();
  await expect(page.getByRole("status")).toContainText("Accepted exact operation");
  await page
    .getByRole("dialog", { name: "Recovery details" })
    .getByRole("button", { name: "Cancel" })
    .click();
  await selectRecoveryView(page, "TERMINAL");
  await inspect(page, identity);
  await expect(page.getByRole("button", { name: "Resolve exact preparation" })).toHaveCount(0);
  await page
    .getByRole("dialog", { name: "Recovery details" })
    .getByRole("button", { name: "Cancel" })
    .click();
  await selectRecoveryView(page, "PENDING");
};

test("exports and retains both exact recovery artifact formats through published Web", async ({
  page,
}) => {
  const caseReference = `RECOVERY-${randomUUID()}`;
  await openAuthenticated(page);
  const prepared = await preparedDecision(page, caseReference);
  await inspect(page, prepared);
  await progress("recovery-export");
  const artifacts = await exportEnvelope(page, prepared);
  await page
    .getByRole("dialog", { name: "Recovery details" })
    .getByRole("button", { name: "Cancel" })
    .click();
  await previewAndRetain(page, "envelope", artifacts.envelope);
  await progress("recovery-envelope-retained");
  await previewAndRetain(page, "record", artifacts.canonicalRecord);
  await progress("recovery-record-retained");
  await inspect(page, prepared);
  await expect(page.getByRole("button", { name: "Resolve exact preparation" })).toBeVisible();
});

test("resolves and dismisses exact preparations with observed server state", async ({ page }) => {
  const caseReference = `RESOLVE-${randomUUID()}`;
  await openAuthenticated(page);
  const prepared = await preparedDecision(page, caseReference);
  await resolveAndObserve(page, prepared);

  await page.getByRole("button", { name: "Cases", exact: true }).click();
  await page.getByRole("button", { name: caseReference }).click();
  await startCommand(page, "Close the case");
  const dismissible = await prepare(page);
  await keepForRecovery(page);
  await navigateRecovery(page);
  await inspect(page, dismissible);
  await page.getByRole("button", { name: "Dismiss preparation" }).click();
  const dismiss = page.getByRole("dialog", { name: "Dismiss preparation?" });
  await expect(dismiss).toContainText(dismissible.requestSha256);
  await expectAccessible(page);
  await dismiss.getByRole("button", { name: "Confirm dismiss" }).click();
  await expect(page.getByRole("status")).toBeVisible();
});

test("preserves operation identity after a dropped published submit response", async ({ page }) => {
  const caseReference = `UNCERTAIN-${randomUUID()}`;
  await openAuthenticated(page);
  await openCase(page, caseReference);
  const identity = await droppedSubmission(page);
  await expect(page.getByRole("button", { name: "Sign out" })).toBeDisabled();
  await page.reload();
  await navigateRecovery(page);
  await selectRecoveryView(page, "TERMINAL");
  await inspect(page, identity);
  await expect(page.getByRole("dialog", { name: "Recovery details" })).toContainText(
    "Observed accepted operation",
  );
  await expect(page.getByRole("button", { name: "Resolve exact preparation" })).toHaveCount(0);
  await page
    .getByRole("dialog", { name: "Recovery details" })
    .getByRole("button", { name: "Cancel" })
    .click();
  await page.getByRole("button", { name: "Operations", exact: true }).click();
  await page.getByLabel("Exact operation ID").fill(identity.operationId);
  await page.getByRole("button", { name: "Observe operation" }).click();
  await expect(
    page.getByText(`Accepted operation ${identity.operationId}`, { exact: false }),
  ).toBeVisible();
});

const retryExactPrepare = async (
  page: Page,
  operationId: string,
  originalBody: string,
): Promise<void> => {
  let calls = 0;
  let sameBody = false;
  await page.route("**/api/v2/operations/prepare", async (route) => {
    calls += 1;
    sameBody = route.request().postData() === originalBody;
    await route.continue();
  });
  await page.getByRole("button", { name: "Retry exact prepare" }).click();
  const review = page.getByRole("dialog", { name: "Review prepared operation" });
  await expect(review).toContainText(operationId);
  expect(calls).toBe(1);
  expect(sameBody).toBe(true);
  await page.unroute("**/api/v2/operations/prepare");
  await review.getByRole("button", { name: "Keep for Recovery" }).click();
};

const observeAcceptedPrepareReplay = async (
  page: Page,
  operationId: string,
  originalBody: string,
): Promise<void> => {
  const token = await sessionToken(page);
  const reply = await browserRequest(page, "/api/v2/operations/prepare", {
    method: "POST",
    headers: { "Content-Type": "application/json", "X-ClaimCore-Antiforgery": token },
    body: originalBody,
  });
  expect(reply.status).toBe(200);
  if (!(await isWebV2Response("command.prepare", reply.payload))) {
    throw new Error("E2E_PREPARE_REPLAY_PROTOCOL");
  }
  const response = reply.payload as WebV2Response<"command.prepare">;
  expect(response.outcome.tag).toBe("OBSERVED_ACCEPTED");
  if (response.outcome.tag === "OBSERVED_ACCEPTED") {
    expect(response.outcome.data.receipt.operationId).toBe(operationId);
  }
};

test("recovers an exact preparation after its published response is dropped", async ({ page }) => {
  await openAuthenticated(page);
  let operationId: string | null = null;
  let originalBody: string | null = null;
  let forwarded = false;
  let calls = 0;
  await page.route("**/api/v2/operations/prepare", async (route) => {
    calls += 1;
    originalBody = route.request().postData();
    const request: unknown = route.request().postDataJSON();
    if (typeof request === "object" && request !== null && "operationId" in request) {
      operationId = typeof request.operationId === "string" ? request.operationId : null;
    }
    const response = await route.fetch();
    forwarded = response.status() === 200;
    await route.abort("connectionfailed");
  });
  await startOpen(page, `PREPARE-LOSS-${randomUUID()}`);
  await page.getByRole("button", { name: "Prepare exact request" }).click();
  await expect(page.getByRole("alert")).toContainText("Recovery");
  expect(calls).toBe(1);
  expect(forwarded).toBe(true);
  if (operationId === null || originalBody === null) {
    throw new Error("E2E_PREPARE_IDENTITY_MISSING");
  }
  await page.unroute("**/api/v2/operations/prepare");
  await expect(page.getByRole("button", { name: "Sign out" })).toBeDisabled();
  await retryExactPrepare(page, operationId, originalBody);
  await page.reload();
  await navigateRecovery(page);
  const row = page.locator(".recovery-list li").filter({ hasText: operationId });
  await expect(row).toHaveCount(1);
  await row.getByRole("button", { name: "Inspect" }).click();
  await expect(page.getByRole("dialog", { name: "Recovery details" })).toContainText(operationId);
  await page.getByRole("button", { name: "Resolve exact preparation" }).click();
  await page
    .getByRole("dialog", { name: "Resolve exact preparation?" })
    .getByRole("button", { name: "Confirm resolve" })
    .click();
  await expect(page.getByRole("status")).toContainText("Accepted exact operation");
  await observeAcceptedPrepareReplay(page, operationId, originalBody);
});
