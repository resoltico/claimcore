import { readFile } from "node:fs/promises";
import { expect, type Locator, type Page } from "@playwright/test";
import type { ResponseFor } from "@contracts/client";
import { retainResponseBodies } from "./response-bodies";
import { observedAccess } from "./work-workflow";

export const confirmAttachmentChange = async (page: Page, path: string) => {
  await retainResponseBodies(page);
  const consent = page.getByRole("dialog").locator("button[data-confirm-operation]");
  await expect(consent).toBeEnabled();
  const reply = page.waitForResponse((response) => new URL(response.url()).pathname === path);
  await consent.click();
  const response = await reply;
  expect(response.status()).toBe(200);
  const value = (await response.json()) as ResponseFor<"operation.execute", "HUMAN">;
  expect(value.outcome.kind).toBe("ACCEPTED");
  return { operationId: value.operationId, access: observedAccess(response.request()) };
};

export const uploadAttachment = async (
  page: Page,
  attachments: Locator,
  name: string,
  portable: string,
  mimeType: string,
  bytes: Buffer,
) => {
  const pick = attachments.getByLabel("Choose a file", { exact: true });
  await expect(pick).toBeEnabled();
  await pick.setInputFiles({ name, mimeType, buffer: bytes });
  const review = page.getByRole("dialog");
  await expect(review).toContainText(portable);
  await expect(review).toContainText("new attachment ID");
  const receipt = await confirmAttachmentChange(page, "/api/attachment.upload");
  const row = attachments.locator(".attachment-list > li").filter({ hasText: portable });
  await expect(row).toHaveCount(1);
  return { row, receipt };
};
export const downloadAttachment = async (page: Page, row: Locator, name: string, bytes: Buffer) => {
  const delivery = page.waitForEvent("download");
  await row.getByRole("button", { name: "Download", exact: true }).click();
  const downloaded = await delivery;
  expect(downloaded.suggestedFilename()).toBe(name);
  const file = await downloaded.path();
  expect(await readFile(file)).toEqual(bytes);
};
