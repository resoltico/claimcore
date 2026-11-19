import { createHash, randomUUID } from "node:crypto";
import { deflateSync } from "node:zlib";
import { expect, test, type Frame, type Locator, type Page } from "@playwright/test";
import type { ResponseFor } from "@contracts/client";
import { openCase } from "./case-workflow";
import { bluePage, redPage, syntheticPdf } from "./attachment-pdf-fixtures";
import {
  confirmAttachmentChange as confirm,
  downloadAttachment as download,
  uploadAttachment as upload,
} from "./attachment-workflow";
import { noHorizontalOverflow } from "./layout-support";
import { browserRequest, expectAccessible, openAuthenticated } from "./session-helpers";
import { recoveryRecords } from "./recovery-storage";
import { workRequest, type observedAccess } from "./work-workflow";

const png = Buffer.from(
  "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==",
  "base64",
);
const jpeg = Buffer.from(
  "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAoHCAkIBgoJCAkMCwoMDxoRDw4ODx8WGBMaJSEnJiQhJCMpLjsyKSw4LCMkM0Y0OD0/QkNCKDFITUhATTtBQj//2wBDAQsMDA8NDx4RER4/KiQqPz8/Pz8/Pz8/Pz8/Pz8/Pz8/Pz8/Pz8/Pz8/Pz8/Pz8/Pz8/Pz8/Pz8/Pz8/Pz8/Pz//wAARCAABAAEDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAb/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/8QAFQEBAQAAAAAAAAAAAAAAAAAABQf/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIRAxEAPwCVAAK4/9k=",
  "base64",
);
const evidence = Buffer.from("original evidence");

const viewerPath = "/attachment-preview/viewer";
const pdfName = "coloured-pages.pdf";
const documentPanel = (page: Page) =>
  page.getByRole("region", { name: "Attachments", exact: true });
const previewDocument = async (row: Locator) => {
  const preview = row.getByRole("button", { name: "Preview", exact: true });
  await expect(preview).toBeEnabled();
  await preview.click();
  const iframe = row.locator("iframe");
  await expect(iframe).toHaveAttribute("title", "Preview");
  return {
    preview,
    iframe,
    status: row.getByRole("status"),
    close: row.getByRole("button", { name: "Close", exact: true }),
  };
};

const viewerFrame = (page: Page) => {
  const frame = page.frame({ url: (url) => url.pathname === viewerPath });
  if (frame === null) {
    throw new Error("Expected the attachment viewer frame.");
  }
  return frame;
};

const centerPixel = (frame: Frame) =>
  frame.locator("canvas").evaluate((canvas) => {
    if (!(canvas instanceof HTMLCanvasElement)) {
      throw new TypeError("Expected the viewer canvas.");
    }
    const context = canvas.getContext("2d")!;
    return Array.from(
      context.getImageData(Math.floor(canvas.width / 2), Math.floor(canvas.height / 2), 1, 1).data,
    );
  });

test("renders and pages through PDFs inside the opaque viewer @engine", async ({ page }) => {
  test.setTimeout(90_000);
  await openAuthenticated(page);
  await openCase(page, `ATTACH-PDF-${randomUUID()}`);
  const bytes = syntheticPdf([redPage, bluePage]);
  const { row } = await upload(
    page,
    documentPanel(page),
    pdfName,
    pdfName,
    "application/pdf",
    bytes,
  );
  await page.evaluate(() => {
    const create = URL.createObjectURL.bind(URL);
    URL.createObjectURL = (object) => {
      if (object instanceof Blob && object.type === "application/pdf") {
        document.documentElement.dataset["pdfBlobCreated"] = "true";
      }
      return create(object);
    };
  });
  const viewer = page.waitForResponse(
    (response) => new URL(response.url()).pathname === viewerPath,
  );
  const controls = await previewDocument(row);
  await expect(controls.close).toBeFocused();
  await expect(controls.iframe).toHaveAttribute("sandbox", "allow-scripts");
  await expect(controls.iframe).toHaveAttribute("tabindex", "-1");
  await expect(controls.status).toHaveAttribute("aria-live", "polite");
  await expect(controls.status).toHaveText("Page 1 of 2");
  const frame = viewerFrame(page);
  expect(await centerPixel(frame)).toEqual([255, 0, 0, 255]);
  const response = await viewer;
  expect(response.status()).toBe(200);
  expect(response.headers()["x-frame-options"]).toBeUndefined();
  const hash = (content: string) => createHash("sha256").update(content).digest("base64");
  const inline = await frame.evaluate(() => {
    const script = document.querySelector("script")?.textContent;
    const style = document.querySelector("style")?.textContent;
    if (!script || !style) {
      throw new Error("The published viewer is missing its inline script or style.");
    }
    return { script, style };
  });
  expect(response.headers()["content-security-policy"]).toContain(
    `script-src 'sha256-${hash(inline.script)}' 'wasm-unsafe-eval'; style-src 'sha256-${hash(inline.style)}'`,
  );

  const sandbox = await frame.evaluate(async () => {
    const violations: string[] = [];
    document.addEventListener("securitypolicyviolation", (event) => {
      violations.push(event.effectiveDirective);
    });
    const denies = (read: () => unknown) => {
      try {
        return read() === "";
      } catch {
        return true;
      }
    };
    const requests = await Promise.allSettled([
      fetch(`${location.origin}/health/live`),
      fetch("https://claimcore-preview-probe.invalid/"),
    ]);
    await new Promise<void>((resolve) => {
      setTimeout(resolve, 50);
    });
    return {
      origin: window.origin,
      cookie: denies(() => document.cookie),
      parentDocument: denies(() => parent.document),
      localStorage: denies(() => localStorage),
      requests: requests.map(({ status }) => status),
      violations,
    };
  });
  expect(sandbox).toMatchObject({
    origin: "null",
    cookie: true,
    parentDocument: true,
    localStorage: true,
    requests: ["rejected", "rejected"],
  });
  expect(sandbox.violations.filter((directive) => directive === "connect-src")).toHaveLength(2);

  const next = row.getByRole("button", { name: "Next page", exact: true });
  await next.focus();
  await next.press("Enter");
  await expect(controls.status).toHaveText("Page 2 of 2");
  expect(await centerPixel(frame)).toEqual([0, 0, 255, 255]);
  await row.getByRole("button", { name: "Previous page", exact: true }).click();
  await expect(controls.status).toHaveText("Page 1 of 2");
  expect(await centerPixel(frame)).toEqual([255, 0, 0, 255]);
  await expect(page.locator("html")).not.toHaveAttribute("data-pdf-blob-created", "true");
  await page.emulateMedia({ forcedColors: "active" });
  expect(await centerPixel(frame)).toEqual([255, 0, 0, 255]);
  await expectAccessible(page);
  await controls.close.click();
  await expect(controls.iframe).toHaveCount(0);
  await expect(controls.preview).toBeFocused();
});

test("discards invalid trapped-frame replies and deadlines preserve download", async ({ page }) => {
  test.setTimeout(90_000);
  await page.addInitScript((path) => {
    if (location.pathname !== path) {
      return;
    }
    window.addEventListener("message", (event: MessageEvent<unknown>) => {
      const [port] = event.ports;
      if (
        event.source !== parent ||
        event.data !== "claimcore-attachment-preview" ||
        port === undefined
      ) {
        return;
      }
      event.stopImmediatePropagation();
      port.onmessage = ({ data }: MessageEvent<{ requestId: number; page: number }>) => {
        if (data.page === 1) {
          port.postMessage({ requestId: data.requestId, page: 1, pageCount: 2 });
        } else if (data.page === 2) {
          for (const reply of [
            { requestId: data.requestId, page: 2, pageCount: 2.5 },
            { requestId: data.requestId, page: 2, pageCount: 1 },
            { requestId: data.requestId + 4, page: 2, pageCount: 2 },
            { requestId: data.requestId, page: 2, pageCount: 2, extra: 0 },
          ]) {
            port.postMessage(reply);
          }
          document.documentElement.dataset["badRepliesSent"] = "true";
        }
      };
    });
  }, viewerPath);
  await openAuthenticated(page);
  await openCase(page, `ATTACH-PDF-REPLY-${randomUUID()}`);
  const bytes = syntheticPdf([redPage, bluePage]);
  const { row } = await upload(
    page,
    documentPanel(page),
    pdfName,
    pdfName,
    "application/pdf",
    bytes,
  );
  const { status, iframe } = await previewDocument(row);
  await expect(status).toHaveText("Page 1 of 2");
  await row.getByRole("button", { name: "Next page", exact: true }).click();
  await expect(viewerFrame(page).locator("html")).toHaveAttribute("data-bad-replies-sent", "true");
  await expect(status).toHaveText("Page 1 of 2");
  await expect(row.getByRole("alert")).toHaveText("This attachment cannot be previewed.", {
    timeout: 22_000,
  });
  await expect(iframe).toHaveCount(0);
  await download(page, row, pdfName, bytes);
});

const navigateWhileRendering = (row: Locator, label: "Next page" | "Close") =>
  row.evaluate((element, command) => {
    const buttons = [...element.querySelectorAll<HTMLButtonElement>(".attachment-preview button")];
    const next = buttons.find((button) => button.textContent === "Next page")!;
    const target = buttons.find((button) => button.textContent === command)!;
    const started = performance.now();
    next.click();
    return new Promise<{ elapsed: number; pending: boolean }>((resolve) => {
      setTimeout(() => {
        const pending = element.querySelector('[role="status"]')?.textContent === "Page 1 of 3";
        target.click();
        resolve({ elapsed: performance.now() - started, pending });
      }, 100);
    });
  }, label);

test("keeps Next and Close responsive during heavy PDF rendering @engine", async ({ page }) => {
  test.setTimeout(120_000);
  await openAuthenticated(page);
  await openCase(page, `ATTACH-PDF-RENDER-${randomUUID()}`);
  const heavy = "0 0 m 5 5 l S\n".repeat(750_000);
  const bytes = syntheticPdf([redPage, { flate: deflateSync(heavy) }, bluePage]);
  const { row } = await upload(
    page,
    documentPanel(page),
    pdfName,
    pdfName,
    "application/pdf",
    bytes,
  );
  const first = await previewDocument(row);
  await expect(first.status).toHaveText("Page 1 of 3");
  const next = await navigateWhileRendering(row, "Next page");
  await expect(first.status).toHaveText("Page 3 of 3");
  await first.close.click();

  const second = await previewDocument(row);
  await expect(second.status).toHaveText("Page 1 of 3");
  const close = await navigateWhileRendering(row, "Close");
  await expect(second.iframe).toHaveCount(0);
  await expect(second.preview).toBeFocused();
  await test.info().attach("pdf-parent-responsiveness", {
    body: JSON.stringify({ next, close }),
    contentType: "application/json",
  });
  for (const result of [next, close]) {
    expect(result.pending).toBe(true);
    expect(result.elapsed).toBeLessThan(700);
  }
});

const previewImage = async (row: Locator, name: string) => {
  const preview = row.getByRole("button", { name: "Preview", exact: true });
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
    "evidence.bin",
    "evidence.bin",
    "application/octet-stream",
    evidence,
  );
  await expect(ordinary.row.getByRole("button", { name: "Preview", exact: true })).toHaveCount(0);
  await download(page, ordinary.row, "evidence.bin", evidence);

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
  await row.getByRole("button", { name: "Preview", exact: true }).click();
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
  expect(recovered.outcome.document.attachmentObservation?.sequence).toBe(
    Number(sequence?.kind === "IDENTIFIER" ? sequence.value : NaN),
  );
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
