import { settingsNavigation } from "./localization-support";
import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "@playwright/test";
import { openAuthenticated } from "./session-helpers";
import { openCase, startAct, reviewDraft } from "./case-workflow";
import { isResponse } from "../src/protocol/client";
import type { ServiceResponse } from "../src/protocol/types";

const dropFirstReply = async (
  page: Page,
  requests: (string | null)[],
  observe: (value: unknown) => void,
) => {
  await page.route("**/api/operation.execute", async (route) => {
    requests.push(route.request().postData());
    if (requests.length === 1) {
      const response = await route.fetch({ maxRedirects: 0 });
      const value: unknown = await response.json();
      observe(value);
      await route.abort("connectionfailed");
    } else {
      await route.continue();
    }
  });
};

const refuseRetryBeforeDispatch = async (page: Page, requests: (string | null)[]) => {
  await page.unroute("**/api/operation.execute");
  await page.route("**/api/operation.execute", async (route) => {
    requests.push(route.request().postData());
    await route.fulfill({
      status: 409,
      contentType: "application/json",
      body: JSON.stringify({
        hostFailure: { code: "CONTRACT_MISMATCH", phase: "NOT_STARTED" },
      }),
    });
  });
  const refused = page.waitForResponse(
    (response) => response.url().endsWith("/api/operation.execute") && response.status() === 409,
  );
  await page.getByRole("button", { name: "Send again", exact: true }).click();
  await (await refused).finished();
  await expect(page.getByRole("button", { name: "Send again", exact: true })).toBeEnabled();
  await expect(
    page.getByRole("heading", {
      name: "We could not confirm whether this change was recorded. Send it again; it cannot be recorded twice.",
      exact: true,
    }),
  ).toBeFocused();
  const returnUrl = page.url();
  await (await settingsNavigation(page)).click();
  await expect(page.locator(".settings-panel")).toBeVisible();
  const signOut = page.getByRole("button", { name: "Sign out", exact: true });
  await expect(signOut).toBeEnabled();
  await signOut.click();
  const consent = page.getByRole("dialog", { name: "Discard these draft changes?" });
  await expect(consent).toBeVisible();
  await consent.getByRole("button", { name: "Keep editing", exact: true }).click();
  await expect(consent).toHaveCount(0);
  await page.locator("[data-return-workspace]").click();
  await expect(page).toHaveURL(returnUrl);
  await expect(page.getByRole("button", { name: "Send again", exact: true })).toBeEnabled();
  await page
    .getByRole("dialog")
    .getByRole("button", { name: "Close", exact: true })
    .first()
    .click();
  const leave = page.getByRole("dialog", { name: "Discard these draft changes?", exact: true });
  await expect(leave).toBeVisible();
  await leave.getByRole("button", { name: "Keep editing", exact: true }).click();
  await expect(page.getByRole("button", { name: "Send again", exact: true })).toBeEnabled();
  await page.unroute("**/api/operation.execute");
  await page.route("**/api/operation.execute", async (route) => {
    requests.push(route.request().postData());
    await route.continue();
  });
};

const expectUnknownOperation = async (page: Page) => {
  await expect(
    page.getByRole("heading", {
      name: "We could not confirm whether this change was recorded. Send it again; it cannot be recorded twice.",
      exact: true,
    }),
  ).toBeFocused();
  await expect(page.getByRole("button", { name: "Send again", exact: true })).toHaveCount(1);
};

test("retries one lost accepted reply with the exact reviewed operation and bytes", async ({
  page,
}) => {
  await openAuthenticated(page);
  await openCase(page, `UNCERTAIN-${randomUUID()}`);
  await startAct(page, "CLOSE", "en");
  const previewed = page.waitForResponse((response) =>
    response.url().endsWith("/api/operation.preview"),
  );
  const identity = await reviewDraft(page);
  const original = (await previewed).request().postData();
  expect(original).not.toBeNull();
  const requests: (string | null)[] = [];
  let accepted: unknown;
  await dropFirstReply(page, requests, (value) => {
    accepted = value;
  });
  await page.getByRole("dialog").locator("button[data-confirm-operation]").click();
  await expectUnknownOperation(page);
  expect(isResponse("operation.execute", accepted)).toBe(true);
  const first = (accepted as ServiceResponse<"operation.execute">).outcome;
  expect(first.kind).toBe("ACCEPTED");
  // Settings parks the same exact attempt; it does not authorize a replay or discard.
  const originalUrl = page.url();
  await (await settingsNavigation(page)).click();
  await expect(page).toHaveURL((url) => url.pathname === "/settings");
  await expect(page.locator(".settings-panel")).toBeVisible();
  await expect(page.getByRole("dialog", { name: "Discard these draft changes?" })).toHaveCount(0);
  await page.locator("[data-return-workspace]").click();
  await expect(page).toHaveURL(originalUrl);
  await expect(page.locator(".settings-panel")).toHaveCount(0);
  expect(requests).toEqual([original]);
  await refuseRetryBeforeDispatch(page, requests);
  const replayed = page.waitForResponse((response) =>
    response.url().endsWith("/api/operation.execute"),
  );
  await page.getByRole("button", { name: "Send again", exact: true }).click();
  const response = await replayed;
  const replay: unknown = await response.json();
  expect(isResponse("operation.execute", replay)).toBe(true);
  const second = (replay as ServiceResponse<"operation.execute">).outcome;
  expect(second.kind).toBe("ALREADY_ACCEPTED");
  if (first.kind !== "ACCEPTED" || second.kind !== "ALREADY_ACCEPTED") {
    throw new Error("Expected accepted replay.");
  }
  const { basis: firstBasis, ...acceptedIdentity } = first.data;
  const { basis: replayBasis, ...replayedIdentity } = second.data;
  expect(replayedIdentity).toEqual(acceptedIdentity);
  const { observedAt: firstObservedAt, ...acceptedBasis } = firstBasis;
  const { observedAt: replayObservedAt, ...otherReplayBasis } = replayBasis;
  expect(otherReplayBasis).toEqual(acceptedBasis);
  // ReceiptObservationTests owns fresh-snapshot time with an independently advanced server clock.
  // This transport oracle permits only that observation to advance, without extra RPCs or a client clock.
  expect(replayObservedAt).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}Z$/u);
  expect(Number.isFinite(Date.parse(replayObservedAt))).toBe(true);
  expect(replayObservedAt >= firstObservedAt).toBe(true);
  expect(second.data.operationId).toBe(identity.operationId);
  expect(second.data.subject.kind === "CASE" ? second.data.subject.revision : null).toBe(2);
  expect(requests).toEqual([original, original, original]);
  await expect(page.locator("#case-detail-title")).toBeVisible();
});
