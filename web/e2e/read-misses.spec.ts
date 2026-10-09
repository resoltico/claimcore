import { randomUUID } from "node:crypto";

import { expect, test } from "@playwright/test";

import { expectAccessible, openAuthenticated } from "./session-helpers";
import { selectLanguage, trackRequests, ui } from "./localization-support";

test("associates malformed operation lookup guidance and prevents empty case lookup [CC-WEB-001]", async ({
  page,
}) => {
  await openAuthenticated(page);
  await expect(page.getByRole("button", { name: "Reload cases", exact: true })).toBeEnabled();
  const requests = trackRequests(page);
  await page.getByLabel("Handler's case reference (exact)").fill("");
  await expect(page.getByRole("button", { name: "Find case", exact: true })).toBeDisabled();
  await page.getByLabel("Handler's case reference (exact)").press("Enter");
  await page.getByRole("button", { name: "Operations", exact: true }).click();
  const operation = page
    .locator('section[aria-labelledby="operation-lookup-title"]')
    .getByRole("textbox");
  await expect(operation).toHaveAccessibleName("Exact operation ID");
  await operation.fill("not-an-operation-id");
  await page.getByRole("button", { name: "Look up recorded result" }).click();
  await expect(operation).toBeFocused();
  await expect(operation).toHaveAttribute("aria-invalid", "true");
  await expect(operation).toHaveAttribute("aria-describedby", /\S/u);
  const before = await page.getByRole("alert").textContent();
  expect(before).toBeTruthy();
  await expect(operation).toHaveAccessibleDescription(before ?? "");
  await selectLanguage(page, "lv");
  await expect(operation).toHaveAccessibleName(ui("lv", "ui.exactOperationId"));
  await expect(page.getByRole("alert")).not.toHaveText(before ?? "");
  await expect(operation).toHaveAttribute("aria-invalid", "true");
  const translated = await page.getByRole("alert").textContent();
  expect(translated).toBeTruthy();
  await expect(operation).toHaveAccessibleDescription(translated ?? "");
  await operation.fill(randomUUID());
  await expect(operation).not.toHaveAttribute("aria-invalid");
  expect(requests).toHaveLength(0);
  await expectAccessible(page);
});

test("hides absent case and operation identities behind neutral access refusals", async ({
  page,
}) => {
  await openAuthenticated(page);
  await page.getByLabel("Handler's case reference (exact)").fill(`ABSENT-${randomUUID()}`);
  await page.getByLabel("Handler's case reference (exact)").press("Enter");
  await expect(page.getByRole("heading", { name: "Case detail" })).toBeVisible();
  await expect(page.getByRole("alert").first()).toBeVisible();
  await expect(page.getByText("Case was not found.", { exact: true })).toHaveCount(0);
  await expect(page.getByText(ui("en", "ui.loadingHistory"), { exact: true })).toHaveCount(0);
  await expectAccessible(page);
  await page.getByRole("button", { name: "Operations", exact: true }).click();
  await page.getByLabel("Exact operation ID").fill(randomUUID());
  await page.getByRole("button", { name: "Look up recorded result" }).click();
  await expect(page.getByRole("alert").first()).toBeVisible();
  await expect(page.getByText("Operation was not observed.", { exact: true })).toHaveCount(0);
  await expectAccessible(page);
});
