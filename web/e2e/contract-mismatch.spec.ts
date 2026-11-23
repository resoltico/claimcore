import { randomUUID } from "node:crypto";
import { expect, test } from "./fixtures";
import { openAuthenticated } from "./session-helpers";
import { startOpen } from "./case-workflow";
import { consumerContractIdentity } from "@contracts/client";

test("a real contract refusal prompts reload without replaying the draft", async ({ page }) => {
  const untouched = await page.request.get("/api/session", {
    headers: { "ClaimCore-Contract": "0".repeat(64) },
  });
  expect(untouched.status()).toBe(409);
  expect(untouched.headers()["claimcore-host-contract"]).toBe(consumerContractIdentity);
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
    expect(response.headers()["claimcore-host-contract"]).toBe(consumerContractIdentity);
    expect(await response.json()).toMatchObject({
      hostFailure: { code: "CONTRACT_MISMATCH", phase: "NOT_STARTED" },
    });
    await route.fulfill({ response });
  });
  await page.getByRole("button", { name: "Review changes" }).click();
  await expect(page.getByRole("alert")).toContainText("ClaimCore was updated. Reload the page.");
  await expect(page.getByRole("button", { name: "Reload page", exact: true })).toBeVisible();
  expect(previews).toBe(1);
  expect(executions).toBe(0);
  await page.getByRole("button", { name: "Reload page", exact: true }).click();
  await expect(page).toHaveURL(/\/cases\/new$/u);
  await expect(
    page.getByRole("dialog").getByRole("heading", { name: "Open a case", exact: true }),
  ).toBeVisible();
  await expect(page.getByRole("textbox", { name: "Case reference", exact: true })).toHaveValue("");
  await expect(page.getByRole("textbox", { name: "Policy number", exact: true })).toHaveValue("");
  expect(previews).toBe(1);
  expect(executions).toBe(0);
});
