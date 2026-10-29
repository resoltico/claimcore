import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "@playwright/test";
import { openCase, reviewDraft, startAct } from "./case-workflow";
import { browserRequest, login, openAuthenticated, expectAccessible } from "./session-helpers";
import { noHorizontalOverflow } from "./layout-support";
import { actPath, casePath } from "../src/routing/routes";
import { freshLoginWorkflowTimeout } from "./admission-window";
import { isResponse } from "../src/protocol/client";

const pauseExecutionReply = async (page: Page) => {
  const received = Promise.withResolvers<void>();
  const released = Promise.withResolvers<void>();
  await page.route("**/api/operation.execute", async (route) => {
    const response = await route.fetch({ maxRedirects: 0 });
    expect(response.status()).toBe(200);
    received.resolve();
    await released.promise;
    await route.fulfill({ response });
  });
  return { received: received.promise, release: released.resolve };
};

test("native beforeunload blocks document navigation only while execution is in flight", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `UNLOAD-${randomUUID()}`;
  await openCase(page, reference);
  await startAct(page, "Close the case");
  await reviewDraft(page);
  const execution = await pauseExecutionReply(page);
  const dialogs: string[] = [];
  page.on("dialog", async (dialog) => {
    dialogs.push(dialog.type());
    await dialog.dismiss();
  });
  try {
    await page.getByText("I confirm these changes.", { exact: true }).click();
    await page.getByRole("button", { name: "Record changes", exact: true }).click();
    await execution.received;
    await expect(page.getByRole("button", { name: "Recording…", exact: true })).toBeDisabled();
    // This is a real document navigation; dismissal aborts it in the browser.
    await page.goto("/operations", { waitUntil: "commit" }).catch(() => null);
    expect(dialogs).toEqual(["beforeunload"]);
    await expect(page).toHaveURL((url) => url.pathname === actPath(reference, "CLOSE"));
    execution.release();
    await expect(page.getByRole("heading", { name: "Case detail", exact: true })).toBeVisible();
    await expect(page.locator(".success-toast")).toContainText("revision 2.");
    await page.goto("/operations", { waitUntil: "commit" });
    await expect(
      page.getByRole("heading", { name: "Operation lookup", exact: true }),
    ).toBeVisible();
    expect(dialogs).toEqual(["beforeunload"]);
  } finally {
    execution.release();
    await page.unroute("**/api/operation.execute");
  }
});

test("an unauthenticated case deep link survives actual OIDC sign-in", async ({
  page,
  browser,
}) => {
  test.setTimeout(freshLoginWorkflowTimeout);
  await openAuthenticated(page);
  const reference = `LOGIN.${randomUUID()}/part?#`;
  await openCase(page, reference);
  const destination = new URL(casePath(reference), page.url());
  const anonymous = await browser.newContext({ ignoreHTTPSErrors: true });
  try {
    const signedOutPage = await anonymous.newPage();
    await signedOutPage.goto(destination.href, { waitUntil: "commit" });
    await expect(signedOutPage.getByRole("link", { name: "Sign in", exact: true })).toBeVisible();
    const session = await browserRequest(signedOutPage, "/api/session");
    expect(isResponse("session", session.payload)).toBe(true);
    if (!isResponse("session", session.payload) || session.payload.outcome.kind !== "SUCCEEDED") {
      throw new Error("E2E_DEEP_LINK_SESSION_UNAVAILABLE");
    }
    expect(session.payload.outcome.data.authenticated).toBe(false);
    await Promise.all([
      signedOutPage.waitForResponse(
        (response) =>
          new URL(response.url()).pathname === "/auth/login" && response.status() === 302,
        { timeout: freshLoginWorkflowTimeout },
      ),
      signedOutPage.waitForResponse(
        (response) =>
          new URL(response.url()).pathname === "/signin-oidc" && response.status() === 302,
        { timeout: freshLoginWorkflowTimeout },
      ),
      login(signedOutPage, "Case detail"),
    ]);
    await expect(signedOutPage).toHaveURL(destination.href);
    await expect(
      signedOutPage.getByRole("heading", { name: "Case detail", exact: true }),
    ).toBeVisible();
    await expect(
      signedOutPage.getByRole("heading", { name: "Case detail", exact: true }),
    ).toContainText(reference);
  } finally {
    await anonymous.close();
  }
});

test("case deep links reload and browser back and forward preserve the reference", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `ROUTE.${randomUUID()}/part?#`;
  await openCase(page, reference);
  await expect(page).toHaveURL((url) => url.pathname === casePath(reference));
  await page.reload();
  await expect(page.getByRole("heading", { name: "Case detail", exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Back to cases", exact: true }).click();
  await expect(page).toHaveURL(/\/cases$/u);
  await page.goBack();
  await expect(page).toHaveURL((url) => url.pathname === casePath(reference));
  await page.goForward();
  await expect(page.getByRole("heading", { name: "Cases", exact: true })).toBeVisible();
});
for (const theme of ["LIGHT", "DARK"] as const) {
  test(`theme ${theme} passes axe and reflows at 320px`, async ({ page }) => {
    await openAuthenticated(page);
    await page.locator("[data-settings-button]").click();
    await page.getByRole("combobox", { name: "Theme", exact: true }).selectOption(theme);
    await page.keyboard.press("Escape");
    await expect(page.locator("html")).toHaveAttribute("data-theme", theme.toLowerCase());
    await expectAccessible(page);
    await page.setViewportSize({ width: 320, height: 900 });
    await noHorizontalOverflow(page, "narrow");
    await expectAccessible(page);
    await page.reload();
    await expect(page.locator("html")).toHaveAttribute("data-theme", theme.toLowerCase());
  });
}
