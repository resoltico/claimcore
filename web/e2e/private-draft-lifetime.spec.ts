import { randomUUID } from "node:crypto";
import { openCase, startAct } from "./case-workflow";
import { expect, test } from "./fixtures";
import { openAuthenticated } from "./session-helpers";
import { fillNamedField } from "./typed-entry";

test("an authored draft survives suspension with its earlier review masked @engine", async ({
  page,
}) => {
  test.setTimeout(90_000);
  const marker = randomUUID().slice(0, 8);
  const fact = `PRIVATE-DRAFT-${marker}`;
  const authored = `AUTHORED-DRAFT-${marker}`;
  await openAuthenticated(page);
  await openCase(page, fact);
  await startAct(page, "case.amendRegistration", "en");
  await fillNamedField(page, "Policy number", authored);
  const dialog = page.getByRole("dialog");
  const editor = dialog.getByRole("textbox", { name: "Policy number", exact: true });
  await expect(editor).toHaveValue(authored);

  const prepared = Promise.withResolvers<void>();
  const release = Promise.withResolvers<void>();
  const served = Promise.withResolvers<void>();
  await page.route(
    "**/api/operation.preview",
    async (route) => {
      try {
        const reply = await route.fetch();
        expect(reply.status()).toBe(200);
        prepared.resolve();
        await release.promise;
        await route.fulfill({ response: reply }).catch(() => undefined);
      } finally {
        served.resolve();
      }
    },
    { times: 1 },
  );

  try {
    await dialog.getByRole("button", { name: "Review changes", exact: true }).click();
    await prepared.promise;
    await page.evaluate(() =>
      window.dispatchEvent(new PageTransitionEvent("pagehide", { persisted: true })),
    );
    await expect(page.locator("body")).not.toContainText(fact);
    await expect(page.locator(".operation-form")).toHaveCount(0);
    await page.evaluate(() =>
      window.dispatchEvent(new PageTransitionEvent("pageshow", { persisted: true })),
    );

    release.resolve();
    await served.promise;
    await expect(page.locator(".case-workspace")).toContainText(fact);
    const reload = dialog.getByRole("button", { name: "Reload current case" });
    await editor.or(reload).waitFor({ state: "visible" });
    if (await reload.isVisible()) {
      await reload.press("Enter");
    }
    await expect(editor).toHaveValue(authored);
    await expect(dialog.locator("button[data-confirm-operation]")).toHaveCount(0);

    const fresh = page.waitForResponse((response) =>
      response.url().endsWith("/api/operation.preview"),
    );
    await dialog.getByRole("button", { name: "Review changes", exact: true }).press("Enter");
    expect((await fresh).ok()).toBe(true);
    await expect(dialog.locator("button[data-confirm-operation]")).toBeVisible();
  } finally {
    release.resolve();
  }
});
