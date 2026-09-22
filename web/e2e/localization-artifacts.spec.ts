import { randomUUID } from "node:crypto";
import { expect, test, type Page } from "@playwright/test";
import { keepForRecovery, openCase, prepare, startCommand, startOpen } from "./case-workflow";
import { exportEnvelope } from "./recovery-artifacts";
import { expectAccessible, openAuthenticated } from "./session-helpers";
import {
  inspectPending,
  noHorizontalOverflow,
  confirmPrepared,
  pauseJsonReply,
  selectFormat,
  selectLanguage,
  trackRequests,
  ui,
} from "./localization-support";

const previewRecord = async (page: Page, bytes: Buffer) => {
  const requests = trackRequests(page);
  const preview = await pauseJsonReply(page, "recovery.importRecordPreview");

  try {
    await page.locator('input[type="file"]').nth(1).setInputFiles({
      name: "exact-record.json",
      mimeType: "application/vnd.claimcore.canonical-command+json",
      buffer: bytes,
    });
    const captured = await preview.ready;
    if (captured.reply.outcome.tag !== "SUCCEEDED")
      throw new Error("E2E_LOCALIZATION_IMPORT_PREVIEW_REFUSED");
    const digest = captured.reply.outcome.data.sourceSha256;
    expect(captured.bytes.equals(bytes)).toBe(true);
    await selectLanguage(page, "ar");
    expect(requests).toHaveLength(1);
    preview.release();
    await expect(page.getByRole("dialog")).toContainText(digest);
    await selectLanguage(page, "lv");
    await selectFormat(page, "ar-EG");
    await expectAccessible(page);
    expect(requests).toHaveLength(1);
    return { digest, requests };
  } finally {
    preview.release();
  }
};

test("retains unchanged canonical file bytes and preview digest through localized import without automatic execution", async ({
  page,
}) => {
  await openAuthenticated(page);
  await openCase(page, `LOCALE-IMPORT-${randomUUID()}`);
  await startCommand(page, "Close the case");
  const identity = await prepare(page);
  await keepForRecovery(page);
  await inspectPending(page, identity);
  const artifacts = await exportEnvelope(page, identity);
  await page.getByRole("dialog").getByRole("button", { name: "Cancel" }).click();
  const { digest, requests } = await previewRecord(page, artifacts.canonicalRecord);
  const retain = await pauseJsonReply(page, "recovery.importRecordRetain");
  try {
    await page.getByRole("button", { name: ui("lv", "ui.retainForRecovery") }).click();
    const captured = await retain.ready;
    expect(captured.bytes.equals(artifacts.canonicalRecord)).toBe(true);
    expect(captured.sourceDigest === digest).toBe(true);
    await selectLanguage(page, "ar");
    expect(requests).toHaveLength(2);
    expect(requests.some((request) => /operations\/submit|recovery\/resolve/u.test(request))).toBe(
      false,
    );
    retain.release();
    await expect(page.getByRole("dialog")).toHaveCount(0);
  } finally {
    retain.release();
  }
});

test("renders exact accepted amounts with RTL and pseudolocale accessibility while canonical copy remains unchanged", async ({
  page,
}) => {
  const amount = "999999999999999999.1234";
  await openAuthenticated(page);
  await startOpen(page, `LOCALE-EXACT-${randomUUID()}`);
  await page.getByLabel("Amount claimed", { exact: true }).fill(amount);
  await prepare(page);
  await confirmPrepared(page);
  await page.getByRole("button", { name: "Submit exact request" }).click();
  await expect(
    page.getByRole("heading", { name: "Accepted operation", exact: true }),
  ).toBeVisible();
  await selectLanguage(page, "ar");
  await selectFormat(page, "ar-EG");
  const row = page.locator('.field-row[data-field-name="claimedAmount"]');
  await expect(row.locator("bdi")).toHaveText("٩٩٩٬٩٩٩٬٩٩٩٬٩٩٩٬٩٩٩٬٩٩٩٫١٢٣٤");
  await expect(page.locator('.field-row[data-field-name="claimedCurrency"] bdi')).toHaveText("EUR");
  await page.evaluate(() =>
    Object.defineProperty(navigator, "clipboard", {
      configurable: true,
      value: { writeText: () => Promise.reject(new Error("synthetic denied clipboard")) },
    }),
  );
  await row.getByRole("button").click();
  await expect(row.getByRole("textbox")).toHaveValue(amount);
  const requests = trackRequests(page);
  await page.setViewportSize({ width: 320, height: 720 });
  await page.emulateMedia({ reducedMotion: "reduce", forcedColors: "active" });
  await noHorizontalOverflow(page);
  await expectAccessible(page);
  await selectFormat(page, "lv-LV");
  await expect(page.locator("html")).toHaveAttribute("lang", "ar");
  await expect(row.getByRole("textbox")).toHaveValue(amount);
  await selectLanguage(page, "en-XA");
  await expectAccessible(page);
  await page.setViewportSize({ width: 640, height: 900 });
  await page.evaluate(() => {
    document.documentElement.style.zoom = "2";
  });
  await noHorizontalOverflow(page);
  await expect(row.getByRole("textbox")).toHaveValue(amount);
  expect(requests).toHaveLength(0);
});
