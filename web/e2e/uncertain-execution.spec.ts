import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "@playwright/test";
import { openAuthenticated } from "./session-helpers";
import { openCase, startCommand, reviewDraft } from "./case-workflow";
import { isResponse } from "../src/protocol/client";
import type { ServiceResponse } from "../src/api/types";

const dropFirstReply = async (
  page: Page,
  requests: (string | null)[],
  observe: (value: unknown) => void,
) => {
  await page.route("**/api/case.execute", async (route) => {
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
  await page.unroute("**/api/case.execute");
  await page.route("**/api/case.execute", async (route) => {
    requests.push(route.request().postData());
    await route.fulfill({
      status: 409,
      contentType: "application/json",
      body: JSON.stringify({
        kind: "HOST_FAILURE",
        code: "CONTRACT_MISMATCH",
        status: 409,
        message: "Synthetic contract refusal",
        diagnostic: { id: "HOST_CONTRACTMISMATCH", parameters: {} },
        executionPhase: "NOT_STARTED",
      }),
    });
  });
  await page.getByRole("button", { name: "Retry the same operation", exact: true }).click();
  await expect(page.getByRole("alert")).toContainText("unknown");
  await expect(page.getByRole("button", { name: "Sign out", exact: true })).toBeDisabled();
  await expect(page.getByRole("button", { name: "Back to case", exact: true })).toHaveCount(0);
  await page.unroute("**/api/case.execute");
  await page.route("**/api/case.execute", async (route) => {
    requests.push(route.request().postData());
    await route.continue();
  });
};

test("retries one lost accepted reply with the exact reviewed operation and bytes [CC-WEB-001]", async ({
  page,
}) => {
  await openAuthenticated(page);
  await openCase(page, `UNCERTAIN-${randomUUID()}`);
  await startCommand(page, "Close the case");
  const previewed = page.waitForResponse((response) =>
    response.url().endsWith("/api/case.preview"),
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
  await expect(
    page.getByRole("button", { name: "Retry the same operation", exact: true }),
  ).toHaveCount(1);
  expect(isResponse("case.execute", accepted)).toBe(true);
  const first = (accepted as ServiceResponse<"case.execute">).outcome;
  expect(first.tag).toBe("ACCEPTED");
  await refuseRetryBeforeDispatch(page, requests);
  const replayed = page.waitForResponse((response) => response.url().endsWith("/api/case.execute"));
  await page.getByRole("button", { name: "Retry the same operation", exact: true }).click();
  const response = await replayed;
  const replay: unknown = await response.json();
  expect(isResponse("case.execute", replay)).toBe(true);
  const second = (replay as ServiceResponse<"case.execute">).outcome;
  expect(second.tag).toBe("ALREADY_ACCEPTED");
  if (first.tag !== "ACCEPTED" || second.tag !== "ALREADY_ACCEPTED") {
    throw new Error("Expected accepted replay.");
  }
  expect(second.data).toEqual(first.data);
  expect(second.data.operationId).toBe(identity.operationId);
  expect(second.data.revision).toBe(2);
  expect(requests).toEqual([original, original, original]);
  await expect(page.getByRole("heading", { name: "Recorded change" })).toBeVisible();
});
