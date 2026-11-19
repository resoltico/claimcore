import { randomUUID } from "node:crypto";
import { readFile } from "node:fs/promises";
import { expect, test, type Locator, type Page } from "@playwright/test";
import type { ResponseFor } from "@contracts/client";
import { openCase } from "./case-workflow";
import { noHorizontalOverflow } from "./layout-support";
import { browserRequest, openAuthenticated } from "./session-helpers";
import { recoveryRecords } from "./recovery-storage";
import { observedAccess, workRequest } from "./work-workflow";

const png = Buffer.from(
  "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==",
  "base64",
);
const jpeg = Buffer.from(
  "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAoHCAkIBgoJCAkMCwoMDxoRDw4ODx8WGBMaJSEnJiQhJCMpLjsyKSw4LCMkM0Y0OD0/QkNCKDFITUhATTtBQj//2wBDAQsMDA8NDx4RER4/KiQqPz8/Pz8/Pz8/Pz8/Pz8/Pz8/Pz8/Pz8/Pz8/Pz8/Pz8/Pz8/Pz8/Pz8/Pz8/Pz8/Pz//wAARCAABAAEDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAb/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/8QAFQEBAQAAAAAAAAAAAAAAAAAABQf/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIRAxEAPwCVAAK4/9k=",
  "base64",
);
const pdf = Buffer.from("%PDF-1.4\n1 0 obj\n<< /Type /Catalog >>\nendobj\n%%EOF\n");

const confirm = async (page: Page, path: string) => {
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

const upload = async (
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
  const receipt = await confirm(page, "/api/attachment.upload");
  const row = attachments.locator(".attachment-list > li").filter({ hasText: portable });
  await expect(row).toHaveCount(1);
  return { row, receipt };
};
const previewImage = async (row: Locator, name: string) => {
  const preview = row.getByRole("button", { name: "Preview image", exact: true });
  await expect(preview).toBeEnabled();
  await preview.focus();
  await preview.press("Enter");
  const img = row.getByRole("img", { name });
  await expect(img).toHaveAttribute("src", /^blob:/u);
  await expect
    .poll(() =>
      img.evaluate((element) => element instanceof HTMLImageElement && element.naturalWidth),
    )
    .toBe(1);
  return { img, preview };
};
const download = async (page: Page, row: Locator, name: string, bytes: Buffer) => {
  const delivery = page.waitForEvent("download");
  await row.getByRole("button", { name: "Download", exact: true }).click();
  const downloaded = await delivery;
  expect(downloaded.suggestedFilename()).toBe(name);
  const file = await downloaded.path();
  expect(await readFile(file)).toEqual(bytes);
};

test("reviews private files, displays native previews and withdraws disclosure @engine", async ({
  page,
}) => {
  test.setTimeout(90_000);
  await openAuthenticated(page);
  const reference = `ATTACH-WEB-${randomUUID()}`;
  await openCase(page, reference);
  const attachments = page.getByRole("region", { name: "Attachments", exact: true });
  const portable = "private_raster_01.png";
  const original = await upload(
    page,
    attachments,
    "private raster 01.png",
    portable,
    "image/png",
    png,
  );
  const raster = await previewImage(original.row, portable);
  const close = original.row.getByRole("button", { name: "Close", exact: true });
  await expect(close).toBeFocused();
  await close.press("Enter");
  await expect(raster.img).toHaveCount(0);
  await expect(raster.preview).toBeFocused();
  await download(page, original.row, portable, png);

  const photograph = await upload(page, attachments, "photo.jpg", "photo.jpg", "image/jpeg", jpeg);
  await previewImage(photograph.row, "photo.jpg");
  await photograph.row.getByRole("button", { name: "Close", exact: true }).click();

  const ordinary = await upload(
    page,
    attachments,
    "document.pdf",
    "document.pdf",
    "application/pdf",
    pdf,
  );
  await expect(
    ordinary.row.getByRole("button", { name: "Preview image", exact: true }),
  ).toHaveCount(0);
  await download(page, ordinary.row, "document.pdf", pdf);

  await raster.preview.click();
  await expect(raster.img).toBeVisible();
  await original.row.getByRole("button", { name: "Withdraw attachment", exact: true }).click();
  await expect(page.getByRole("dialog")).toContainText("storage limits");
  await confirm(page, "/api/operation.execute");
  await expect(raster.img).toHaveCount(0);
  await expect(attachments).not.toContainText(portable);
  const { operationId, access } = original.receipt;
  const old = await workRequest(page, "operation.get", { operationId }, access);
  expect(old.outcome.kind).toBe("ACCEPTED");
  expect(JSON.stringify(old)).not.toContain(portable);
  await expect(page.locator(".case-timeline-panel")).not.toContainText(portable);
  await page.setViewportSize({ width: 320, height: 900 });
  await expect(attachments.getByLabel("Choose a file", { exact: true })).toBeVisible();
  await noHorizontalOverflow(page, "narrow");
});

test("settles a lost upload acknowledgement with an identity-only recovery record", async ({
  page,
}) => {
  await openAuthenticated(page);
  await openCase(page, `ATTACH-RECOVER-${randomUUID()}`);
  const panel = page.getByRole("region", { name: "Attachments", exact: true });
  await panel.getByLabel("Choose a file", { exact: true }).setInputFiles({
    name: "recovery canary.bin",
    mimeType: "application/octet-stream",
    buffer: Buffer.from("exact original"),
  });
  const consent = page.getByRole("dialog").locator("button[data-confirm-operation]");
  await expect(consent).toBeEnabled();

  let operationId: string | null = null;
  let sends = 0;
  await page.route("**/api/attachment.upload", async (route) => {
    sends += 1;
    const pending = await recoveryRecords(page);
    expect(pending).toHaveLength(1);
    expect(pending[0]).toMatchObject({ format: 2, act: "attachment.add", subject: "ATTACHMENT" });
    const record = pending[0]!;
    expect(Object.keys(record).sort()).toEqual([
      "act",
      "actorId",
      "attemptId",
      "format",
      "installationId",
      "operationId",
      "serviceOrigin",
      "subject",
    ]);
    const committed = await route.fetch({ maxRedirects: 0 });
    const reply = (await committed.json()) as ResponseFor<"operation.execute", "HUMAN">;
    expect(reply.outcome.kind).toBe("ACCEPTED");
    expect(reply.operationId).toBe(record["operationId"]);
    ({ operationId } = reply);
    await route.abort("connectionfailed");
  });
  const observed = page.waitForResponse(async (response) => {
    if (
      operationId === null ||
      !response.url().endsWith("/api/operation.get") ||
      response.status() !== 200
    ) {
      return false;
    }
    const reply = (await response.json()) as ResponseFor<"operation.get", "HUMAN">;
    return reply.operationId === operationId && reply.outcome.kind === "ACCEPTED";
  });
  await consent.click();
  await observed;
  await expect.poll(() => recoveryRecords(page)).toEqual([]);
  await expect(panel.locator(".attachment-list > li")).toHaveCount(1);
  await expect(panel).toContainText("recovery_canary.bin");
  expect(sends).toBe(1);
});

test("a rejected session masks the previously disclosed attachments", async ({ page }) => {
  await openAuthenticated(page);
  await openCase(page, `ATTACH-SESSION-${randomUUID()}`);
  const panel = page.getByRole("region", { name: "Attachments", exact: true });
  const name = "session-canary.png";
  const { row } = await upload(page, panel, name, name, "image/png", png);
  await page.context().clearCookies({ name: "__Host-ClaimCoreSession" });
  const refusal = page.waitForResponse(
    (response) => new URL(response.url()).pathname === "/api/attachment.download",
  );
  await row.getByRole("button", { name: "Download", exact: true }).click();
  const response = await refusal;
  expect(response.status()).toBe(401);
  expect(await response.json()).toMatchObject({ hostFailure: { code: "SESSION_REJECTED" } });
  await expect(row).toHaveCount(0);
  await expect(page.getByText("You are signed out", { exact: false }).first()).toBeVisible();
});

const holdPreviewDigest = async (page: Page) => {
  await page.evaluate(() => {
    const digest = crypto.subtle.digest.bind(crypto.subtle);
    const released = new Promise<void>((resolve) => {
      document.addEventListener(
        "attachment-test-release-digest",
        () => {
          resolve();
        },
        { once: true },
      );
    });
    crypto.subtle.digest = async (algorithm, bytes) => {
      const hash = await digest(algorithm, bytes);
      document.documentElement.dataset["attachmentDigest"] = "held";
      await released;
      document.documentElement.dataset["attachmentDigest"] = "released";
      return hash;
    };
  });
};

test("drops a preview verified after observing withdrawal in another tab", async ({ page }) => {
  test.setTimeout(90_000);
  await openAuthenticated(page);
  await openCase(page, `ATTACH-LATE-${randomUUID()}`);
  const panel = page.getByRole("region", { name: "Attachments", exact: true });
  const name = "delayed-preview.png";
  const { row } = await upload(page, panel, name, name, "image/png", png);
  await holdPreviewDigest(page);
  await row.getByRole("button", { name: "Preview image", exact: true }).click();
  const html = page.locator("html");
  await expect(html).toHaveAttribute("data-attachment-digest", "held");

  const withdrawal = await page.context().newPage();
  await withdrawal.goto(page.url());
  const withdrawalRow = withdrawal.locator(".attachment-list > li").filter({ hasText: name });
  await withdrawalRow.getByRole("button", { name: "Withdraw attachment", exact: true }).click();
  await confirm(withdrawal, "/api/operation.execute");
  await expect(withdrawalRow).toHaveCount(0);

  const timeline = page.locator(".case-timeline-panel");
  await timeline.getByRole("tab", { name: "Attachments", exact: true }).click();
  await expect(row).toHaveCount(0);
  await page.evaluate(() => {
    document.dispatchEvent(new Event("attachment-test-release-digest"));
  });
  await expect(html).toHaveAttribute("data-attachment-digest", "released");
  await expect(panel.getByRole("img", { name })).toHaveCount(0);
});

test("masks the case workspace when an admitted attachment list rejects the session", async ({
  page,
}) => {
  await openAuthenticated(page);
  await openCase(page, `ATTACH-LIST-${randomUUID()}`);
  const entered = Promise.withResolvers<void>();
  const release = Promise.withResolvers<void>();
  await page.route("**/api/case.attachments", async (route) => {
    entered.resolve();
    await release.promise;
    const headers = await route.request().allHeaders();
    headers["cookie"] = (headers["cookie"] ?? "")
      .split(";")
      .filter((cookie) => !cookie.trim().startsWith("__Host-ClaimCoreSession="))
      .join(";");
    const response = await route.fetch({ headers, maxRedirects: 0 });
    await route.fulfill({ response });
  });
  const attempt = page.waitForResponse(
    (response) => new URL(response.url()).pathname === "/api/case.attachments",
  );
  const facts = page.locator(".case-workspace .entries-view [data-entry='policyNumber']");
  await page.reload({ waitUntil: "domcontentloaded" });
  await entered.promise;
  try {
    await expect(facts).toBeVisible();
    await page.context().clearCookies({ name: "__Host-ClaimCoreSession" });
  } finally {
    release.resolve();
  }
  const refusal = await attempt;
  expect(refusal.status()).toBe(401);
  expect(await refusal.json()).toMatchObject({ hostFailure: { code: "SESSION_REJECTED" } });
  await expect(facts).toHaveCount(0);
  await expect(page.getByText("You are signed out", { exact: false }).first()).toBeVisible();
});

const expectWithdrawalObservation = async (
  page: Page,
  operationId: string,
  access: ReturnType<typeof observedAccess>,
) => {
  const { status, payload } = await browserRequest(page, "/api/operation.get", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "ClaimCore-Representation": "HUMAN",
      "ClaimCore-Installation": access.installationId,
      "X-ClaimCore-Antiforgery": access.token,
    },
    body: JSON.stringify({ operationId }),
  });
  expect(status).toBe(200);
  const recovered = payload as ResponseFor<"operation.get", "HUMAN">;
  if (recovered.outcome.kind !== "ACCEPTED") {
    throw new Error("Expected the recorded withdrawal.");
  }
  const details = recovered.outcome.document.screen.regions.flatMap(({ content }) =>
    content.kind === "ENTRIES" ? content.entries : [],
  );
  const sequence = details.find((entry) => entry.key === "sequence")?.value;
  expect(sequence?.kind).toBe("IDENTIFIER");
  expect(details.find((entry) => entry.key === "attachmentDisclosure")?.value).toEqual({
    kind: "COUNT",
    value: Number(sequence?.kind === "IDENTIFIER" ? sequence.value : NaN),
  });
};

test("confirmed withdrawal stays hidden through a list fault and a delayed timeline", async ({
  page,
}) => {
  await openAuthenticated(page);
  await openCase(page, `ATTACH-WITHDRAW-FAULT-${randomUUID()}`);
  const panel = page.getByRole("region", { name: "Attachments", exact: true });
  const name = "withdrawal_fault_canary.png";
  const { row, receipt } = await upload(page, panel, name, name, "image/png", png);
  const { img } = await previewImage(row, name);
  const history = page.locator(".case-timeline-panel");
  await expect(history).toContainText(name);
  await row.getByRole("button", { name: "Withdraw attachment", exact: true }).click();
  await expect(page.getByRole("dialog").locator("button[data-confirm-operation]")).toBeEnabled();
  const timeline = Promise.withResolvers<void>();
  await page.route("**/api/case.timeline", async (route) => {
    await timeline.promise;
    await route.continue();
  });
  await page.route("**/api/case.attachments", async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      headers: { "ClaimCore-Installation": receipt.access.installationId },
      json: {
        representation: "HUMAN",
        outcome: {
          kind: "FAILED",
          fault: {
            code: "CORE_STORE_UNAVAILABLE",
            action: "RETRY_SAFE",
            messageId: "fault.CORE_STORE_UNAVAILABLE",
          },
        },
      },
    });
  });
  const fault = page.waitForResponse(
    (response) => new URL(response.url()).pathname === "/api/case.attachments",
  );
  try {
    const withdrawal = await confirm(page, "/api/operation.execute");
    expect(await (await fault).json()).toMatchObject({
      outcome: { fault: { code: "CORE_STORE_UNAVAILABLE" } },
    });
    await expect(panel.getByRole("alert")).toBeVisible();
    await expect(row).toHaveCount(0);
    await expect(img).toHaveCount(0);
    await expect(panel.getByRole("button", { name: "Download" })).toHaveCount(0);
    await expect(
      panel.getByRole("button", { name: "Withdraw attachment", exact: true }),
    ).toHaveCount(0);
    await expect(history).not.toContainText(name);

    await expectWithdrawalObservation(page, withdrawal.operationId, receipt.access);
  } finally {
    timeline.resolve();
  }
});
