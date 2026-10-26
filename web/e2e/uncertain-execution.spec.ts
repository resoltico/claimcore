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
  await expect(page.getByRole("alert")).toContainText("unknown");
  await expect(
    page.getByRole("heading", {
      name: "The operation outcome is unknown. Retry the exact same operation.",
      exact: true,
    }),
  ).toBeFocused();
  await expect(page.getByRole("button", { name: "Sign out", exact: true })).toBeDisabled();
  await expect(page.getByRole("button", { name: "Back to case", exact: true })).toHaveCount(0);
  await page.unroute("**/api/operation.execute");
  await page.route("**/api/operation.execute", async (route) => {
    requests.push(route.request().postData());
    await route.continue();
  });
};

const expectUnknownOperation = async (page: Page) => {
  await expect(
    page.getByRole("heading", {
      name: "The operation outcome is unknown. Retry the exact same operation.",
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
  await startAct(page, "Close the case");
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
  await page.getByText("I confirm these changes.", { exact: true }).click();
  await page.getByRole("button", { name: "Record changes" }).click();
  await expect(page.getByRole("alert")).toContainText("unknown");
  await expectUnknownOperation(page);
  expect(isResponse("operation.execute", accepted)).toBe(true);
  const first = (accepted as ServiceResponse<"operation.execute">).outcome;
  expect(first.kind).toBe("ACCEPTED");
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
  expect(second.data).toEqual(first.data);
  expect(second.data.operationId).toBe(identity.operationId);
  expect(second.data.subject.kind === "CASE" ? second.data.subject.revision : null).toBe(2);
  expect(requests).toEqual([original, original, original]);
  await expect(page.getByRole("heading", { name: "Recorded change" })).toBeVisible();
});
