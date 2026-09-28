import { readFile, writeFile } from "node:fs/promises";

import { expect, type Download, type Page } from "@playwright/test";

import { isWebV3Response } from "../src/generated/convergence/web-v3.validation";
import type { PreparedIdentity } from "./case-workflow";
import { expectAccessible, progress } from "./session-helpers";

type Exported = Readonly<{ envelope: Buffer }>;

const assertEnvelope = (bytes: Buffer, identity: PreparedIdentity): void => {
  const decoded: unknown = JSON.parse(bytes.toString("utf8"));
  if (typeof decoded !== "object" || decoded === null) throw new Error("Invalid envelope JSON.");
  const artifact = decoded as Record<string, unknown>;
  if (artifact["operationId"] !== identity.operationId) {
    throw new Error("Recovery envelope operation identity mismatch.");
  }
  const hasEncryptedFields = ["ciphertextBase64", "macSha256"].every(
    (key) => typeof artifact[key] === "string",
  );
  if (
    artifact["format"] !== "claimcore-recovery-artifact" ||
    artifact["formatVersion"] !== 3 ||
    !hasEncryptedFields ||
    "canonicalRequestBase64" in artifact
  ) {
    throw new Error("Recovery envelope is not the encrypted v3 format.");
  }
};

const downloadBytes = async (download: Download): Promise<Buffer> => {
  const path = await download.path();
  if (path === null) throw new Error("Recovery download has no private test path.");
  return readFile(path);
};

const captureDispositionShape = async (
  header: string,
  identity: PreparedIdentity,
): Promise<void> => {
  const expected = `claimcore-recovery-${identity.operationId}.json`;
  const parts = header.split(";").map((part) => {
    const trimmed = part.trim();
    if (trimmed.toLowerCase() === "attachment") return "attachment";
    const [name, ...valueParts] = trimmed.split("=");
    const value = valueParts.join("=");
    if (name === undefined || !["filename", "filename*"].includes(name.toLowerCase())) {
      return "other-parameter";
    }
    const normalized = value.replace(/^"|"$/gu, "");
    const encoded = normalized.startsWith("UTF-8''");
    const filename = encoded ? normalized.slice(7) : normalized;
    return `${name.toLowerCase()}:${encoded ? "utf8" : "plain"}:${filename === expected ? "expected" : "other"}`;
  });
  const engine = process.env["CLAIMCORE_WEB_E2E_ENGINE"];
  if (engine === undefined || !["chromium", "firefox", "webkit"].includes(engine)) return;
  const report = new URL(`../../artifacts/browser/export-header-${engine}.json`, import.meta.url);
  await writeFile(report, `${JSON.stringify({ parts })}\n`);
};

export const exportEnvelope = async (page: Page, identity: PreparedIdentity): Promise<Exported> => {
  let exportCalls = 0;
  await page.route("**/api/v3/recovery/export", async (route) => {
    exportCalls += 1;
    await route.continue();
  });
  const exportButton = page.getByRole("button", { name: "Export recovery envelope" });
  if ((await exportButton.count()) !== 1) throw new Error("E2E_EXPORT_ACTION_UNAVAILABLE");
  await exportButton.click();
  await progress("export-warning-open");
  const warning = page.getByRole("dialog", { name: "Export recovery envelope?" });
  await expect(warning).toContainText("claimant");
  await expectAccessible(page);
  expect(exportCalls).toBe(0);
  await warning.getByRole("button", { name: "Cancel" }).click();
  await progress("export-cancelled");
  expect(exportCalls).toBe(0);
  await exportButton.click();
  await progress("export-reconfirmed");
  const responseEvent = page.waitForResponse(
    (response) => new URL(response.url()).pathname === "/api/v3/recovery/export",
  );
  const event = page.waitForEvent("download");
  await warning.getByRole("button", { name: "Confirm export" }).click();
  await progress("export-dispatched");
  const response = await responseEvent;
  await progress("export-response");
  await captureDispositionShape(response.headers()["content-disposition"] ?? "", identity);
  if (response.status() !== 200) throw new Error("E2E_EXPORT_HTTP_FAILURE");
  const download = await event;
  await progress("export-downloaded");
  expect(exportCalls).toBe(1);
  expect(download.suggestedFilename()).toBe(`claimcore-recovery-${identity.operationId}.json`);
  const envelope = await downloadBytes(download);
  await page.unroute("**/api/v3/recovery/export");
  assertEnvelope(envelope, identity);
  return { envelope };
};

export const previewAndRetain = async (page: Page, bytes: Buffer): Promise<void> => {
  await page.locator('input[type="file"]').nth(0).setInputFiles({
    name: "synthetic-recovery.json",
    mimeType: "application/vnd.claimcore.recovery+json",
    buffer: bytes,
  });
  const dialog = page.getByRole("dialog", { name: "Retain imported recovery material?" });
  await expect(dialog).toContainText("never submitted automatically");
  await expectAccessible(page);
  const path = "/api/v3/recovery/import-envelope/retain";
  const responseEvent = page.waitForResponse((response) => response.url().endsWith(path));
  await dialog.getByRole("button", { name: "Retain for Recovery" }).click();
  const response = await responseEvent;
  const payload: unknown = await response.json();
  const endpoint = "recovery.importEnvelopeRetain";
  if (!(await isWebV3Response(endpoint, payload))) {
    throw new Error("Invalid import-retain response.");
  }
  expect(["RETAINED", "EXISTING"]).toContain((payload as { outcome: { tag: string } }).outcome.tag);
  await expect(dialog).not.toBeVisible();
};
