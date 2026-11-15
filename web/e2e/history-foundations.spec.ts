import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { openAuthenticated, expectAccessible } from "./session-helpers";
import { startOpen } from "./case-workflow";

// Actual browser traversal is essential: synthetic popstate cannot prove Forward preservation.
test("@engine preserves dirty drafts and the Forward branch through Settings and declined departure", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `HISTORY-${randomUUID()}`;
  await startOpen(page, reference);
  const draft = page.getByRole("textbox", { name: "Case reference", exact: true });
  await expect(draft).toHaveValue(reference);
  await page.getByRole("button", { name: "Settings", exact: true }).click();
  await expect(page).toHaveURL(/\/settings$/u);
  const length = await page.evaluate(() => history.length);
  await page.goBack();
  await expect(page).toHaveURL(/\/cases\/new$/u);
  await expect(draft).toHaveValue(reference);
  await page.goForward();
  await expect(page).toHaveURL(/\/settings$/u);
  await page.goBack();
  await expect(page).toHaveURL(/\/cases\/new$/u);
  await page.goBack();
  const guard = page.getByRole("dialog", { name: "Discard these draft changes?" });
  await expect(guard).toBeVisible();
  await expect(page).toHaveURL(/\/cases\/new$/u);
  await guard.getByRole("button", { name: "Keep editing" }).click();
  await expect(draft).toHaveValue(reference);
  await page.goForward();
  await expect(page).toHaveURL(/\/settings$/u);
  expect(await page.evaluate(() => history.length)).toBe(length);
  await page.goBack();
  await page.goBack();
  await expect(guard).toBeVisible();
  await guard.getByRole("button", { name: "Discard and leave" }).click();
  await expect(page).not.toHaveURL(/\/cases\/new$/u);
  expect(await page.evaluate(() => history.length)).toBe(length);
  await expectAccessible(page);
});

test("@engine requests a native reload warning after activation and preserves the cancelled draft", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `EXIT-${randomUUID()}`;
  await startOpen(page, reference);
  const warning = page.waitForEvent("dialog");
  await page.evaluate(() => {
    setTimeout(() => {
      location.reload();
    }, 0);
  });
  const dialog = await warning;
  expect(dialog.type()).toBe("beforeunload");
  await dialog.dismiss();
  await expect(page.getByRole("textbox", { name: "Case reference", exact: true })).toHaveValue(
    reference,
  );
});

test("@engine preserves the intended multi-entry target during repeated pending traversals", async ({
  page,
}) => {
  await openAuthenticated(page);
  const target = page.url();
  const reference = `MULTI-${randomUUID()}`;
  await startOpen(page, reference);
  await page.getByRole("button", { name: "Settings", exact: true }).click();
  await expect(page).toHaveURL(/\/settings$/u);
  const length = await page.evaluate(() => history.length);
  await page.evaluate(() => {
    history.go(-2);
  });
  const guard = page.getByRole("dialog", { name: "Discard these draft changes?" });
  await expect(guard).toBeVisible();
  await expect(page).toHaveURL(/\/settings$/u);
  await page.evaluate(() => {
    history.back();
    history.forward();
    history.go(-2);
  });
  await expect(guard).toBeVisible();
  await expect(page).toHaveURL(/\/settings$/u);
  await guard.getByRole("button", { name: "Discard and leave" }).click();
  await expect(page).toHaveURL(target);
  expect(await page.evaluate(() => history.length)).toBe(length);
});
