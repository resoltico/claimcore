import { openRecovery, showExactLookup, showOperationLookup } from "./workspace-navigation";
import { randomUUID } from "node:crypto";

import { expect, test } from "@playwright/test";

import { expectAccessible, openAuthenticated } from "./session-helpers";
import { selectLanguage, trackForegroundRequests, ui } from "./localization-support";
import { endpoints } from "@contracts/client";

test("associates malformed operation lookup guidance and prevents empty case lookup", async ({
  page,
}) => {
  await openAuthenticated(page);
  await expect(page.getByRole("button", { name: "Reload cases", exact: true })).toBeEnabled();
  const lookup = await showExactLookup(page);
  const requests = trackForegroundRequests(page);
  await lookup.fill("");
  await expect(
    page.getByRole("button", { name: "Open by exact reference", exact: true }),
  ).toBeDisabled();
  await lookup.press("Enter");
  expect(requests).toHaveLength(0);
  await openRecovery(page);
  const operation = await showOperationLookup(page);
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
  const observe = endpoints.find((endpoint) => endpoint.id === "operation.observe");
  expect(observe).toBeDefined();
  expect(requests).toEqual([`POST ${observe?.path}`]);
  await expectAccessible(page);
});

test("hides absent case and operation identities behind neutral access refusals", async ({
  page,
}) => {
  await openAuthenticated(page);
  await (await showExactLookup(page)).fill(`ABSENT-${randomUUID()}`);
  await (await showExactLookup(page)).press("Enter");
  await expect(page.locator("#case-detail-title")).toBeVisible();
  await expect(page.getByRole("alert").first()).toBeVisible();
  await expect(page.getByText("Case was not found.", { exact: true })).toHaveCount(0);
  await expect(page.getByText(ui("en", "ui.loadingHistory"), { exact: true })).toHaveCount(0);
  await expectAccessible(page);
  await openRecovery(page);
  await (await showOperationLookup(page)).fill(randomUUID());
  await page.getByRole("button", { name: "Look up recorded result" }).click();
  await expect(page.getByRole("alert").first()).toBeVisible();
  await expect(page.getByText("Operation was not observed.", { exact: true })).toHaveCount(0);
  await expectAccessible(page);
});
