import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { openAuthenticated } from "./session-helpers";
import { startOpen } from "./case-workflow";
import { consumerContractIdentity } from "@contracts/client";

test("a real contract refusal prompts reload without replaying the draft", async ({ page }) => {
  await openAuthenticated(page);
  await startOpen(page, `MISMATCH-${randomUUID()}`);
  let previews = 0;
  let executions = 0;
  page.on("request", (request) => {
    if (request.url().endsWith("/api/operation.execute")) {
      executions += 1;
    }
  });
  await page.route("**/api/operation.preview", async (route) => {
    previews += 1;
    const headers = { ...route.request().headers(), "claimcore-contract": "0".repeat(64) };
    expect(headers["claimcore-contract"]).not.toBe(consumerContractIdentity);
    const response = await route.fetch({ headers, maxRedirects: 0 });
    expect(response.status()).toBe(409);
    expect(await response.json()).toMatchObject({
      hostFailure: { code: "CONTRACT_MISMATCH", phase: "NOT_STARTED" },
    });
    await route.fulfill({ response });
  });
  await page.getByRole("button", { name: "Review changes" }).click();
  await expect(page.getByRole("alert")).toContainText("does not match these browser assets");
  await expect(page.getByRole("button", { name: "Reload page", exact: true })).toBeVisible();
  expect(previews).toBe(1);
  expect(executions).toBe(0);
  await page.getByRole("button", { name: "Reload page", exact: true }).click();
  await expect(page).toHaveURL(/\/cases\/new$/u);
  await expect(page.getByRole("heading", { name: "Open a case", exact: true })).toBeVisible();
  await expect(
    page.getByRole("textbox", { name: "Handler's case reference", exact: true }),
  ).toHaveValue("");
  await expect(page.getByRole("textbox", { name: "Claimant name", exact: true })).toHaveValue("");
  expect(previews).toBe(1);
  expect(executions).toBe(0);
});
