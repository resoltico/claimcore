import { randomUUID } from "node:crypto";
import { expect, test } from "./fixtures";
import type { ResponseFor } from "@contracts/client";
import { openAuthenticated, expectAccessible } from "./session-helpers";
import { openCase, startOpen } from "./case-workflow";

test("confirmed compact departure focuses Recovery and restores the initiating link on Back @engine", async ({
  page,
}) => {
  await openAuthenticated(page);
  await openCase(page, `GUARD-FOCUS-${randomUUID().slice(0, 8)}`);
  await page.setViewportSize({ width: 320, height: 900 });
  const draft = page.locator('[data-region="NOTES"]').getByRole("textbox", { name: "Note" });
  await draft.fill("Keep this unsaved note until departure is confirmed.");
  const caseUrl = page.url();
  const menu = page.locator(".shell-more");
  const trigger = page.locator("#workspace-more");
  const recovery = menu.getByRole("link", { name: /^Unconfirmed changes/u });
  const guard = page.getByRole("dialog", { name: "Discard these draft changes?" });
  await trigger.press("Enter");
  await recovery.press("Enter");
  await expect(guard).toBeVisible();
  await guard.getByRole("button", { name: "Keep editing" }).press("Enter");
  await expect(page).toHaveURL(caseUrl);
  await expect(draft).toHaveValue("Keep this unsaved note until departure is confirmed.");
  await expect(recovery).toBeFocused();

  await page.keyboard.press("Enter");
  await guard.getByRole("button", { name: "Discard and leave" }).press("Enter");
  await expect(page).toHaveURL(/\/recovery$/u);
  await expect(guard).toHaveCount(0);
  await expect(
    page.getByRole("heading", { name: "Unconfirmed changes", exact: true }),
  ).toBeFocused();
  await expect(recovery).toBeHidden();

  await page.goBack();
  await expect(page).toHaveURL(caseUrl);
  await expect(draft).toHaveValue("");
  await expect(trigger).toBeFocused();
  await expect(menu).toBeHidden();
});

test("Back returns focus to More before an unrelated Cases read finishes @engine", async ({
  page,
}) => {
  await openAuthenticated(page);
  await page.setViewportSize({ width: 320, height: 900 });
  await page.goto("/cases?view=ALL");
  const queue = page.locator(".worklist");
  await expect(queue).toHaveAttribute("aria-busy", "false");
  const menu = page.locator(".shell-more");
  const trigger = page.locator("#workspace-more");
  const recovery = menu.getByRole("link", { name: /^Unconfirmed changes/u });
  const people = menu.getByRole("link", { name: "People and access", exact: true });
  await trigger.press("Enter");
  await recovery.press("Enter");
  await expect(
    page.getByRole("heading", { name: "Unconfirmed changes", exact: true }),
  ).toBeFocused();

  const held = Promise.withResolvers<void>();
  const release = Promise.withResolvers<void>();
  await page.route("**/api/case.list", async (route) => {
    const reply = await route.fetch();
    expect(reply.status()).toBe(200);
    held.resolve();
    await release.promise;
    await route.fulfill({ response: reply });
  });
  try {
    await page.goBack();
    await held.promise;
    await expect(queue).toHaveAttribute("aria-busy", "true");
    await expect(trigger).toBeFocused();
    await trigger.press("Enter");
    await expect(people).toBeVisible();
    await people.focus();
    await expect(people).toBeFocused();
  } finally {
    release.resolve();
  }
  await expect(queue).toHaveAttribute("aria-busy", "false");
  await expect(people).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(page).toHaveURL(/\/people$/u);
});

test("Back focuses the Cases heading while loading when a refreshed requester loses a shell link @engine", async ({
  page,
  browserName,
}) => {
  await openAuthenticated(page);
  await page.setViewportSize({ width: 320, height: 900 });
  await page.goto("/cases?view=ALL");
  const queue = page.locator(".worklist");
  await expect(queue).toHaveAttribute("aria-busy", "false");
  const menu = page.locator(".shell-more");
  const trigger = page.locator("#workspace-more");
  const people = menu.locator('a[href="/people"]');
  const history = menu.locator('a[href="/people-history"]');
  await trigger.press("Enter");
  await expect(history).toBeVisible();
  await people.press("Enter");
  await expect(page).toHaveURL(/\/people$/u);

  await page.route("**/api/people.list", (route) =>
    route.fulfill({
      json: {
        representation: "HUMAN",
        outcome: {
          kind: "REJECTED",
          rejection: {
            kind: "REFUSED",
            advice: {
              action: "ASK_FOR_ACCESS",
              message: { messageId: "diagnostic.RESOURCE_UNAVAILABLE", arguments: [] },
            },
          },
        },
      },
    }),
  );
  await page.route("**/api/context", async (route) => {
    const response = await route.fetch();
    const reply = (await response.json()) as ResponseFor<"context", "HUMAN">;
    if (reply.outcome.kind !== "SUCCEEDED") {
      throw new Error("A current requester is required for the narrower fixture.");
    }
    const { document } = reply.outcome;
    await route.fulfill({
      response,
      json: {
        ...reply,
        outcome: {
          ...reply.outcome,
          document: {
            ...document,
            actions: [],
            links: document.links.filter(
              ({ link }) => link.endpoint !== "people.list" && link.endpoint !== "people.history",
            ),
          },
        },
      },
    });
  });
  const refresh = page.waitForResponse(
    (response) => new URL(response.url()).pathname === "/api/context",
  );
  await page.clock.setFixedTime(new Date(Date.now() + 31_000));
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
  expect((await refresh).status()).toBe(200);
  // Cases is offered only after the refreshed requester has settled.
  await expect(page.locator('.workspace-mobile-tabs a[href="/cases"]')).toBeVisible();
  await expect(people).toHaveCount(0);
  await expect(history).toHaveCount(0);

  const held = Promise.withResolvers<void>();
  const release = Promise.withResolvers<void>();
  await page.route("**/api/case.list", async (route) => {
    const reply = await route.fetch();
    expect(reply.status()).toBe(200);
    held.resolve();
    await release.promise;
    await route.fulfill({ response: reply });
  });
  const all = queue.getByRole("tab", { selected: true });
  try {
    await page.goBack();
    await held.promise;
    await expect(queue).toHaveAttribute("aria-busy", "true");
    await expect(queue.getByRole("heading", { name: "Cases", exact: true })).toBeFocused();
    await expect(menu).toBeHidden();
    const tabsBar = queue.locator(".view-tabs-bar");
    expect(await tabsBar.evaluate((bar) => bar.scrollWidth > bar.clientWidth)).toBe(true);
    await page.keyboard.press(browserName === "webkit" ? "Alt+Tab" : "Tab");
    await expect(all).toBeFocused();
    await expect(all).toBeInViewport({ ratio: 1 });
  } finally {
    release.resolve();
  }
  await expect(queue).toHaveAttribute("aria-busy", "false");
  await expect(all).toBeFocused();
});

// Actual browser traversal is essential: synthetic popstate cannot prove Forward preservation.
test("@engine preserves dirty drafts and the Forward branch through Settings and declined departure", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `HISTORY-${randomUUID().slice(0, 8)}`;
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
  const reference = `EXIT-${randomUUID().slice(0, 8)}`;
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
  const reference = `MULTI-${randomUUID().slice(0, 8)}`;
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
