import { randomUUID } from "node:crypto";
import { readFile } from "node:fs/promises";
import { expect, test, type Page, type Download } from "@playwright/test";
import { openCase, startCommand, prepare, keepForRecovery } from "./case-workflow";
import { trackRequests } from "./localization-support";
import { openAuthenticated, expectAccessible } from "./session-helpers";

const pausedExport = async (page: Page, loseReply: boolean) => {
  let release: () => void = () => undefined;
  let captured: (body: string) => void = () => undefined;
  const waiting = new Promise<void>((resolve) => {
    release = resolve;
  });
  const ready = new Promise<string>((resolve) => {
    captured = resolve;
  });
  let calls = 0;
  await page.route("**/api/v3/recovery/export", async (route) => {
    calls += 1;
    const response = await route.fetch();
    expect(response.status()).toBe(200);
    captured(route.request().postData() ?? "");
    await waiting;
    if (loseReply) {
      await route.abort("connectionfailed");
    } else {
      await route.fulfill({ response });
    }
  });
  return { ready, release, calls: () => calls };
};

const verifyDownloadedIdentity = async (download: Promise<Download>, operationId: string) => {
  const file = await download;
  expect(file.suggestedFilename()).toBe(`claimcore-recovery-${operationId}.json`);
  const path = await file.path();
  if (path === null) {
    throw new Error("Synthetic download path is unavailable.");
  }
  const artifact: unknown = JSON.parse(await readFile(path, "utf8"));
  if (typeof artifact !== "object" || artifact === null || !("operationId" in artifact)) {
    throw new Error("Synthetic downloaded envelope is malformed.");
  }
  expect(artifact.operationId).toBe(operationId);
};

const inspectRetainedClose = async (page: Page) => {
  await openAuthenticated(page);
  await openCase(page, `DISPATCH-${randomUUID()}`);
  await startCommand(page, "Close the case");
  const identity = await prepare(page);
  await keepForRecovery(page);
  await page.getByRole("button", { name: "Recovery", exact: true }).click();
  const row = page.locator(".recovery-list li").filter({ hasText: identity.operationId });
  await row.getByRole("button", { name: "Inspect", exact: true }).click();
  return { identity, row };
};

for (const loseReply of [false, true]) {
  test(`keeps recovery controls and exact identity stable after closing a real delayed export (${loseReply ? "lost reply" : "download"}) [CC-REC-001]`, async ({
    page,
  }) => {
    const { identity, row } = await inspectRetainedClose(page);
    const requests = trackRequests(page);
    const pending = await pausedExport(page, loseReply);
    try {
      await page.getByRole("button", { name: "Export recovery envelope", exact: true }).click();
      const download = loseReply ? null : page.waitForEvent("download");
      await page.getByRole("button", { name: "Confirm export", exact: true }).click();
      expect(await pending.ready).toBe(JSON.stringify(identity));
      await page.keyboard.press("Escape");
      await expect(page.getByRole("dialog", { name: "Recovery details" })).toHaveCount(0);
      const inspect = row.getByRole("button", { name: "Inspect", exact: true });
      const importing = page.getByRole("button", { name: "Import recovery envelope", exact: true });
      await expect(inspect).toBeDisabled();
      await expect(importing).toBeDisabled();
      await expect(page.getByRole("button", { name: "Cases", exact: true })).toBeDisabled();
      await expect(page.getByRole("button", { name: "Sign out", exact: true })).toBeDisabled();
      await inspect.evaluate((element: HTMLButtonElement) => {
        element.click();
      });
      await importing.evaluate((element: HTMLButtonElement) => {
        element.click();
      });
      expect(pending.calls()).toBe(1);
      expect(requests).toEqual(["POST /api/v3/recovery/export"]);
      await expectAccessible(page);
      pending.release();
      if (download === null) {
        await expect(page.getByRole("alert")).toContainText(identity.operationId);
      } else {
        await verifyDownloadedIdentity(download, identity.operationId);
      }
      await expect(inspect).toBeEnabled();
      await expect(importing).toBeEnabled();
      await expect(page.getByRole("button", { name: "Sign out", exact: true })).toBeEnabled();
      await expect(page.getByRole("dialog", { name: "Recovery details" })).toHaveCount(0);
      expect(pending.calls()).toBe(1);
      expect(requests).toEqual(["POST /api/v3/recovery/export"]);
    } finally {
      pending.release();
      await page.unroute("**/api/v3/recovery/export");
    }
  });
}
