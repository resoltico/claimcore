import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { openAuthenticated } from "./session-helpers";
import { startOpen } from "./case-workflow";
import { wireContractFingerprint } from "../src/generated/contracts/endpoint-catalog";

test("a real contract refusal prompts reload without replaying the draft [CC-WEB-001]", async ({
  page,
}) => {
  await openAuthenticated(page);
  await startOpen(page, `MISMATCH-${randomUUID()}`);
  let previews = 0;
  let executions = 0;
  page.on("request", (request) => {
    if (request.url().endsWith("/api/command.execute")) {
      executions += 1;
    }
  });
  await page.route("**/api/command.preview", async (route) => {
    previews += 1;
    const headers = { ...route.request().headers(), "claimcore-contract": "0".repeat(64) };
    expect(headers["claimcore-contract"]).not.toBe(wireContractFingerprint);
    const response = await route.fetch({ headers, maxRedirects: 0 });
    expect(response.status()).toBe(409);
    expect(await response.json()).toMatchObject({
      code: "CONTRACT_MISMATCH",
      executionPhase: "NOT_STARTED",
    });
    await route.fulfill({ response });
  });
  await page.getByRole("button", { name: "Review changes" }).click();
  await expect(page.getByRole("alert")).toContainText("does not match these browser assets");
  await expect(page.getByRole("button", { name: "Reload page", exact: true })).toBeVisible();
  expect(previews).toBe(1);
  expect(executions).toBe(0);
  await page.getByRole("button", { name: "Reload page", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Cases", exact: true })).toBeVisible();
  expect(previews).toBe(1);
  expect(executions).toBe(0);
});
