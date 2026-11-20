import { createHash, randomUUID } from "node:crypto";
import { readFile } from "node:fs/promises";
import { deflateSync } from "node:zlib";
import {
  errors,
  expect,
  test,
  type Frame,
  type Locator,
  type Page,
  type Request,
} from "@playwright/test";
import type { ResponseFor } from "@contracts/client";
import { openCase } from "./case-workflow";
import { bluePage, filledWidgetPdf, redPage, syntheticPdf } from "./attachment-pdf-fixtures";
import {
  confirmAttachmentChange as confirm,
  downloadAttachment as download,
  uploadAttachment as upload,
} from "./attachment-workflow";
import { noHorizontalOverflow } from "./layout-support";
import { selectLanguage } from "./localization-support";
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

// macOS WebKit's default Tab behavior skips buttons. Option+Tab includes them.
const pagerTab = (browserName: string) =>
  browserName === "webkit" && process.platform === "darwin" ? "Alt+Tab" : "Tab";

test("refuses retained attachment metadata before confirmation or upload @engine", async ({
  page,
}) => {
  await openAuthenticated(page);
  await openCase(page, `ATTACH-DUPLICATE-${randomUUID()}`);
  const attachments = documentPanel(page);
  await upload(page, attachments, "initial.txt", "initial.txt", "text/plain", evidence);
  const uploads: string[] = [];
  page.on("request", (request) => {
    const path = new URL(request.url()).pathname;
    if (path === "/api/attachment.upload") {
      uploads.push(path);
    }
  });
  const preview = page.waitForResponse(
    (response) => new URL(response.url()).pathname === "/api/operation.preview",
  );
  await attachments.getByLabel("Choose a file", { exact: true }).setInputFiles({
    name: "another-name.txt",
    mimeType: "text/plain",
    buffer: evidence,
  });
  const result: unknown = await (await preview).json();
  expect(result).toMatchObject({
    outcome: {
      kind: "REJECTED",
      rejection: {
        advice: { message: { messageId: "diagnostic.ATTACHMENT_DUPLICATE" } },
      },
    },
  });
  await expect(page.getByRole("dialog").locator("[data-confirm-operation]")).toHaveCount(0);
  expect(uploads).toEqual([]);
  await expect(attachments.locator(".attachment-list > li")).toHaveCount(1);
});

test("renders and pages through PDFs inside the opaque viewer @engine", async ({
  page,
  browserName,
}) => {
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
    const script = document.querySelector("script")?.textContent ?? "";
    const style = document.querySelector("style")?.textContent ?? "";
    if (script.length === 0 || style.length === 0) {
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
  const previous = row.getByRole("button", { name: "Previous page", exact: true });
  await expect(previous).toBeDisabled();
  await page.keyboard.press(pagerTab(browserName));
  await expect(next).toBeFocused();
  await page.keyboard.down("Enter");
  try {
    await expect(controls.status).toHaveText("Page 2 of 2");
    await expect(next).toBeDisabled();
    await expect(previous).toBeFocused();
    expect(await centerPixel(frame)).toEqual([0, 0, 255, 255]);
    await page.keyboard.down("Enter");
    await expect(previous).toBeFocused();
    await expect(controls.status).toHaveText("Page 2 of 2");
  } finally {
    await page.keyboard.up("Enter");
  }
  await page.keyboard.press("Enter");
  await expect(controls.status).toHaveText("Page 1 of 2");
  await expect(previous).toBeDisabled();
  await expect(next).toBeFocused();
  expect(await centerPixel(frame)).toEqual([255, 0, 0, 255]);
  await expect(page.locator("html")).not.toHaveAttribute("data-pdf-blob-created", "true");
  await page.emulateMedia({ forcedColors: "active" });
  expect(await centerPixel(frame)).toEqual([255, 0, 0, 255]);
  await expectAccessible(page);
  await page.keyboard.press(`Shift+${pagerTab(browserName)}`);
  await expect(controls.close).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(controls.iframe).toHaveCount(0);
  await expect(controls.preview).toBeFocused();
});

test("keeps the PDF pager usable in narrow Arabic and forced colors @engine", async ({
  page,
  browserName,
}) => {
  await openAuthenticated(page);
  await openCase(page, `ATTACH-RTL-${randomUUID()}`);
  await upload(
    page,
    documentPanel(page),
    pdfName,
    pdfName,
    "application/pdf",
    syntheticPdf([redPage, bluePage]),
  );
  await selectLanguage(page, "ar");
  await page.setViewportSize({ width: 390, height: 844 });
  await page.emulateMedia({ forcedColors: "active" });

  const panel = page.getByRole("region", { name: "المرفقات", exact: true });
  await expect(panel.getByLabel("اختر ملفًا", { exact: true })).toBeVisible();
  const row = panel.locator(".attachment-list > li").filter({ hasText: pdfName });
  const preview = row.getByRole("button", { name: "معاينة", exact: true });
  await preview.focus();
  await page.keyboard.press("Enter");
  const iframe = row.locator("iframe");
  await expect(iframe).toHaveAttribute("title", "معاينة");
  const close = row.getByRole("button", { name: "إغلاق", exact: true });
  const next = row.getByRole("button", { name: "الصفحة التالية", exact: true });
  const previous = row.getByRole("button", { name: "الصفحة السابقة", exact: true });
  const status = row.getByRole("status");
  await expect(status).toHaveText("الصفحة \u20681\u2069 من \u20682\u2069");
  await expect(close).toBeFocused();
  await page.keyboard.press(pagerTab(browserName));
  await expect(next).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(status).toHaveText("الصفحة \u20682\u2069 من \u20682\u2069");
  await expect(previous).toBeFocused();
  expect(await centerPixel(viewerFrame(page))).toEqual([0, 0, 255, 255]);
  await noHorizontalOverflow(page, "narrow");
  await expectAccessible(page);
  await page.keyboard.press("Enter");
  await expect(status).toHaveText("الصفحة \u20681\u2069 من \u20682\u2069");
  await expect(next).toBeFocused();
  await page.keyboard.press(`Shift+${pagerTab(browserName)}`);
  await expect(close).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(iframe).toHaveCount(0);
  await expect(preview).toBeFocused();
});

test("paints a filled PDF widget into the canvas without interactive elements @engine", async ({
  page,
}) => {
  await openAuthenticated(page);
  await openCase(page, `ATTACH-WIDGET-${randomUUID()}`);
  const { row } = await upload(
    page,
    documentPanel(page),
    pdfName,
    pdfName,
    "application/pdf",
    filledWidgetPdf(),
  );
  const controls = await previewDocument(row);
  await expect(controls.status).toHaveText("Page 1 of 1");
  const frame = viewerFrame(page);
  expect(await centerPixel(frame)).toEqual([255, 0, 0, 255]);
  expect(
    await frame
      .locator("body")
      .evaluate((body) => Array.from(body.children, (element) => element.localName).toSorted()),
  ).toEqual(["canvas", "script"]);
});

for (const moveFocusOutside of [false, true]) {
  test(`discards invalid trapped-frame replies and preserves download with focus ${moveFocusOutside ? "outside" : "inside"} the pager @engine`, async ({
    page,
    browserName,
  }) => {
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
    const { status, iframe, close } = await previewDocument(row);
    await expect(status).toHaveText("Page 1 of 2");
    await page.keyboard.press(pagerTab(browserName));
    await expect(row.getByRole("button", { name: "Next page", exact: true })).toBeFocused();
    await page.keyboard.press("Enter");
    await expect(row.getByRole("button", { name: "Previous page", exact: true })).toBeFocused();
    await expect(viewerFrame(page).locator("html")).toHaveAttribute(
      "data-bad-replies-sent",
      "true",
    );
    await expect(status).toHaveText("Page 1 of 2");
    const downloadButton = row.getByRole("button", { name: "Download", exact: true });
    if (moveFocusOutside) {
      await downloadButton.focus();
    }
    await expect(row.getByRole("alert")).toHaveText("This attachment cannot be previewed.", {
      timeout: 22_000,
    });
    await expect(iframe).toHaveCount(0);
    await expect(moveFocusOutside ? downloadButton : close).toBeFocused();
    await download(page, row, pdfName, bytes);
  });
}

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

test("a delayed PDF preview preserves deliberately moved keyboard focus @engine", async ({
  page,
}) => {
  test.setTimeout(90_000);
  await openAuthenticated(page);
  await openCase(page, `ATTACH-PDF-FOCUS-${randomUUID()}`);
  const { row } = await upload(
    page,
    documentPanel(page),
    pdfName,
    pdfName,
    "application/pdf",
    syntheticPdf([redPage]),
  );
  const preview = row.getByRole("button", { name: "Preview", exact: true });
  const close = row.getByRole("button", { name: "Close", exact: true });
  const frame = row.locator("iframe");
  const rendered = row.getByRole("status");
  const other = page.locator(".case-timeline-panel").getByRole("tab", {
    name: "Attachments",
    exact: true,
  });
  const held = Promise.withResolvers<void>();
  const release = Promise.withResolvers<void>();
  await page.route(
    "**/api/attachment.preview",
    async (route) => {
      const response = await route.fetch({ maxRedirects: 0 });
      expect(response.status()).toBe(200);
      expect(response.headers()["content-type"]).toBe("application/pdf");
      held.resolve();
      await release.promise;
      await route.fulfill({ response });
    },
    { times: 1 },
  );

  await preview.focus();
  await page.keyboard.press("Enter");
  try {
    await held.promise;
    await expect(frame).toHaveCount(0);
    await other.focus();
    await expect(other).toBeFocused();
  } finally {
    release.resolve();
  }
  await expect(rendered).toHaveText("Page 1 of 1");
  await expect(other).toBeFocused();

  await close.focus();
  await page.keyboard.press("Enter");
  await expect(frame).toHaveCount(0);
  await expect(preview).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(rendered).toHaveText("Page 1 of 1");
  await expect(close).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(frame).toHaveCount(0);
  await expect(preview).toBeFocused();
});

for (const transfer of ["preview", "download"] as const) {
  test(`keyboard focus survives a held attachment ${transfer} @engine`, async ({ page }) => {
    test.setTimeout(90_000);
    await openAuthenticated(page);
    await openCase(page, `ATTACH-TRANSFER-FOCUS-${randomUUID()}`);
    const name = "unsupported-preview.png";
    const bytes = Buffer.from("synthetic bytes outside the PNG profile");
    const panel = documentPanel(page);
    const { row } = await upload(page, panel, name, name, "image/png", bytes);
    const trigger = row.getByRole("button", {
      name: transfer === "preview" ? "Preview" : "Download",
      exact: true,
    });
    const path = `/api/attachment.${transfer}`;
    const held = Promise.withResolvers<void>();
    const release = Promise.withResolvers<void>();
    await page.route(
      `**${path}`,
      async (route) => {
        const response = await route.fetch({ maxRedirects: 0 });
        held.resolve();
        await release.promise;
        await route.fulfill({ response });
      },
      { times: 1 },
    );

    const delivery = transfer === "download" ? page.waitForEvent("download") : null;
    await trigger.focus();
    try {
      await page.keyboard.press("Enter");
      await held.promise;
      await expect(trigger).toBeFocused();
      await expect(trigger).toHaveAttribute("aria-disabled", "true");
      await expect(trigger).not.toHaveAttribute("disabled");
      await expect(trigger).toHaveCSS("cursor", "not-allowed");
      const repeated = page
        .waitForRequest((request) => new URL(request.url()).pathname === path, {
          timeout: 750,
        })
        .then(
          () => true,
          (error: unknown) => {
            if (error instanceof errors.TimeoutError) {
              return false;
            }
            throw error;
          },
        );
      await page.keyboard.press("Enter");
      expect(await repeated).toBe(false);
    } finally {
      release.resolve();
    }

    if (delivery === null) {
      await expect(panel.getByRole("alert")).toHaveText("This attachment cannot be previewed.");
    } else {
      const downloaded = await delivery;
      expect(downloaded.suggestedFilename()).toBe(name);
      expect(await readFile(await downloaded.path())).toEqual(bytes);
    }
    await expect(trigger).toBeEnabled();
    await expect(trigger).toBeFocused();
    await expect(trigger).toHaveCSS("cursor", "pointer");
  });
}

for (const [transfer, key] of [
  ["preview", "Enter"],
  ["download", "Enter"],
  ["download", "Space"],
] as const) {
  test(`held ${key} cannot activate attachment ${transfer} across settlement @engine`, async ({
    page,
  }) => {
    test.setTimeout(90_000);
    await openAuthenticated(page);
    await openCase(page, `ATTACH-SETTLEMENT-${randomUUID()}`);
    const bytes = syntheticPdf([redPage]);
    const { row } = await upload(
      page,
      documentPanel(page),
      pdfName,
      pdfName,
      "application/pdf",
      bytes,
    );
    const trigger = row.getByRole("button", {
      name: transfer === "preview" ? "Preview" : "Download",
      exact: true,
    });
    const close = row.getByRole("button", { name: "Close", exact: true });
    const frame = row.locator("iframe");
    const status = row.getByRole("status");
    const path = `/api/attachment.${transfer}`;
    const isTransfer = (request: Request) => new URL(request.url()).pathname === path;
    let requests = 0;
    page.on("request", (request) => {
      if (isTransfer(request)) {
        requests++;
      }
    });
    const held = Promise.withResolvers<void>();
    const release = Promise.withResolvers<void>();
    await page.route(
      `**${path}`,
      async (route) => {
        const response = await route.fetch({ maxRedirects: 0 });
        held.resolve();
        await release.promise;
        await route.fulfill({ response });
      },
      { times: 1 },
    );

    const delivery = transfer === "download" ? page.waitForEvent("download") : null;
    await trigger.focus();
    await page.keyboard.press("Enter");
    try {
      await held.promise;
      await expect(trigger).toHaveAttribute("aria-disabled", "true");
      await expect(trigger).toBeFocused();
      await page.keyboard.down(key);
    } finally {
      release.resolve();
    }
    try {
      if (delivery === null) {
        await expect(status).toHaveText("Page 1 of 1");
        await expect(close).toBeFocused();
      } else {
        const downloaded = await delivery;
        expect(await readFile(await downloaded.path())).toEqual(bytes);
        await expect(trigger).toBeEnabled();
      }
      if (key === "Enter") {
        await page.keyboard.down(key);
      }
    } finally {
      await page.keyboard.up(key);
    }
    const queued = await page
      .waitForRequest(isTransfer, { timeout: 750 })
      .catch((error: unknown) => {
        if (error instanceof errors.TimeoutError) {
          return null;
        }
        throw error;
      });
    expect(queued).toBeNull();
    expect(requests).toBe(1);

    if (transfer === "preview") {
      await expect(frame).toHaveCount(1);
      await expect(close).toBeFocused();
      await page.keyboard.press("Enter");
      await expect(frame).toHaveCount(0);
    }
    await expect(trigger).toBeFocused();
    const separate = page.waitForRequest(isTransfer);
    const nextDownload = transfer === "download" ? page.waitForEvent("download") : null;
    await page.keyboard.press(key);
    await separate;
    if (nextDownload === null) {
      await expect(status).toHaveText("Page 1 of 1");
    } else {
      await nextDownload;
    }
    expect(requests).toBe(2);
  });
}

for (const { focusOutside, pointerTarget, gesture } of [
  { focusOutside: false, pointerTarget: null, gesture: "keyboard" },
  { focusOutside: true, pointerTarget: null, gesture: "keyboard" },
  { focusOutside: false, pointerTarget: "input", gesture: "mouse" },
  { focusOutside: true, pointerTarget: "label", gesture: "mouse" },
  { focusOutside: false, pointerTarget: "input", gesture: "touch" },
  { focusOutside: true, pointerTarget: "label", gesture: "touch" },
  { focusOutside: false, pointerTarget: "input", gesture: "touch cancel" },
] as const) {
  const useTouch = gesture === "touch" || gesture === "touch cancel";
  const name =
    pointerTarget === null
      ? `a failed attachment file read preserves ${focusOutside ? "external" : "picker"} focus`
      : `a held ${useTouch ? gesture : "pointer gesture"} on the attachment picker ${pointerTarget} stays inactive after settlement`;
  test(`${name} ${useTouch ? "@chromium" : "@engine"}`, async ({ page, browserName }) => {
    test.skip(useTouch && browserName !== "chromium", "Split touch gestures require Chromium CDP.");
    test.setTimeout(90_000);
    await page.addInitScript(() => {
      let blockFirstRead = true;
      File.prototype.arrayBuffer = function arrayBuffer(this: File) {
        if (this.name === "pending-selection.txt") {
          document.documentElement.dataset["secondFileRead"] = "started";
        }
        if (this.name === "unreadable.txt" && blockFirstRead) {
          blockFirstRead = false;
          document.documentElement.dataset["attachmentRead"] = "held";
          return new Promise<ArrayBuffer>((_resolve, reject) => {
            document.addEventListener(
              "reject-attachment-read",
              () => {
                reject(new DOMException("Synthetic unreadable file", "NotReadableError"));
              },
              { once: true },
            );
          });
        }
        return Blob.prototype.arrayBuffer.call(this);
      };
    });
    await openAuthenticated(page);
    await openCase(page, `ATTACH-PICKER-FOCUS-${randomUUID()}`);
    const panel = documentPanel(page);
    const picker = panel.getByLabel("Choose a file", { exact: true });
    const external = page.locator(".case-timeline-panel").getByRole("tab", {
      name: "Attachments",
      exact: true,
    });
    const file = {
      name: "unreadable.txt",
      mimeType: "text/plain",
      buffer: Buffer.from("synthetic file"),
    };
    const chooseFromKeyboard = async () => {
      const chooser = page.waitForEvent("filechooser");
      await page.keyboard.press("Enter");
      return chooser;
    };

    const pointerControl =
      pointerTarget === "label"
        ? panel.locator("label").filter({ hasText: "Choose a file" })
        : picker;
    const pointAtControl = async () => {
      await pointerControl.scrollIntoViewIfNeeded();
      const box = await pointerControl.boundingBox();
      if (box === null) {
        throw new Error("Expected a visible picker control.");
      }
      return { x: box.x + Math.min(box.width / 2, 24), y: box.y + box.height / 2 };
    };
    const touch = useTouch ? await page.context().newCDPSession(page) : null;
    if (touch !== null) {
      await touch.send("Emulation.setTouchEmulationEnabled", { enabled: true, maxTouchPoints: 1 });
    }
    const startPointer = async ({ x, y }: { x: number; y: number }) => {
      if (touch === null) {
        await page.mouse.move(x, y);
        await page.mouse.down();
      } else {
        await touch.send("Input.dispatchTouchEvent", {
          type: "touchStart",
          touchPoints: [{ x, y }],
        });
      }
    };
    const finishPointer = async (cancel = false) => {
      if (touch === null) {
        await page.mouse.up();
      } else {
        await touch.send("Input.dispatchTouchEvent", {
          type: cancel ? "touchCancel" : "touchEnd",
          touchPoints: [],
        });
      }
    };
    const observeChooser = () =>
      page.waitForEvent("filechooser", { timeout: 750 }).catch((error: unknown) => {
        if (error instanceof errors.TimeoutError) {
          return null;
        }
        throw error;
      });
    let choosers = 0;
    page.on("filechooser", () => {
      choosers++;
    });

    await picker.focus();
    await (await chooseFromKeyboard()).setFiles(file);
    const html = page.locator("html");
    let pointerDown = false;
    try {
      try {
        await expect(html).toHaveAttribute("data-attachment-read", "held");
        await expect(picker).toBeFocused();
        await expect(picker).toHaveAttribute("aria-disabled", "true");
        await expect(picker).not.toHaveAttribute("disabled");

        const busyChooser = observeChooser();
        await page.keyboard.press("Enter");
        expect(await busyChooser).toBeNull();
        await picker.setInputFiles({ ...file, name: "pending-selection.txt" });
        await expect(html).not.toHaveAttribute("data-second-file-read");
        if (focusOutside) {
          await external.focus();
          await expect(external).toBeFocused();
        }
        if (pointerTarget !== null) {
          await startPointer(await pointAtControl());
          pointerDown = true;
        }
      } finally {
        await page.evaluate(() => {
          document.dispatchEvent(new Event("reject-attachment-read"));
        });
      }
      await expect(panel.getByRole("alert")).toHaveText(
        "Complete this input using the required format.",
      );
      await expect(picker).toBeEnabled();
      if (pointerDown) {
        const queuedChooser = observeChooser();
        await finishPointer(gesture === "touch cancel");
        pointerDown = false;
        expect(await queuedChooser).toBeNull();
      }
      expect(choosers).toBe(1);
      await expect(focusOutside ? external : picker).toBeFocused();
    } finally {
      if (pointerDown) {
        await finishPointer(true);
      }
    }

    if (pointerTarget === null) {
      if (focusOutside) {
        await picker.focus();
      }
      await (await chooseFromKeyboard()).setFiles(file);
    } else {
      const point = await pointAtControl();
      const fresh = page.waitForEvent("filechooser");
      await startPointer(point);
      await finishPointer();
      await (await fresh).setFiles(file);
    }
    expect(choosers).toBe(2);
    await expect(page.getByRole("dialog")).toContainText(file.name);
  });
}

test("held Enter closes a PDF preview once before a separate press can reopen it @engine", async ({
  page,
}) => {
  test.setTimeout(90_000);
  await openAuthenticated(page);
  await openCase(page, `ATTACH-PDF-CLOSE-${randomUUID()}`);
  const { row } = await upload(
    page,
    documentPanel(page),
    pdfName,
    pdfName,
    "application/pdf",
    syntheticPdf([redPage]),
  );
  const preview = row.getByRole("button", { name: "Preview", exact: true });
  const close = row.getByRole("button", { name: "Close", exact: true });
  const frame = row.locator("iframe");
  const isPreviewRequest = (request: Request) =>
    new URL(request.url()).pathname === "/api/attachment.preview";
  await preview.focus();
  await page.keyboard.press("Enter");
  await expect(row.getByRole("status")).toHaveText("Page 1 of 1");
  await expect(close).toBeFocused();

  const repeated = page.waitForRequest(isPreviewRequest, { timeout: 1500 }).then(
    () => true,
    (error: unknown) => {
      if (error instanceof errors.TimeoutError) {
        return false;
      }
      throw error;
    },
  );
  await page.keyboard.down("Enter");
  try {
    await page.keyboard.down("Enter");
    await page.keyboard.down("Enter");
  } finally {
    await page.keyboard.up("Enter");
  }
  expect(await repeated).toBe(false);
  await expect(frame).toHaveCount(0);
  await expect(preview).toBeFocused();

  const separate = page.waitForRequest(isPreviewRequest);
  await page.keyboard.press("Enter");
  await separate;
  await expect(row.getByRole("status")).toHaveText("Page 1 of 1");
  await expect(close).toBeFocused();
});

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

test("withdrawal closes a pending PDF frame and discards its queued page reply @engine", async ({
  page,
}) => {
  test.setTimeout(90_000);
  await page.addInitScript(() => {
    if (window !== window.top) {
      return;
    }
    const channels = window.MessageChannel;
    const descriptor = Object.getOwnPropertyDescriptor(MessagePort.prototype, "onmessage")!;
    window.MessageChannel = class extends channels {
      constructor() {
        super();
        const { port1 } = this;
        Object.defineProperty(port1, "onmessage", {
          get(): unknown {
            return descriptor.get?.call(port1);
          },
          set(handler: ((event: MessageEvent<unknown>) => void) | null) {
            if (handler === null) {
              descriptor.set?.call(port1, null);
              return;
            }
            descriptor.set?.call(port1, (event: MessageEvent<unknown>) => {
              const { data } = event;
              if (
                typeof data !== "object" ||
                data === null ||
                !("page" in data) ||
                data.page !== 2 ||
                !("requestId" in data) ||
                data.requestId !== 2 ||
                !("pageCount" in data) ||
                data.pageCount !== 2
              ) {
                handler.call(port1, event);
                return;
              }
              document.documentElement.dataset["pdfReply"] = "held";
              document.addEventListener(
                "release-pdf-reply",
                () => {
                  // The real ACK was queued in the parent while the viewer was alive.
                  const clear = window.clearTimeout;
                  let cleared = false;
                  window.clearTimeout = (timeout) => {
                    cleared = true;
                    clear(timeout);
                  };
                  try {
                    handler.call(port1, event);
                  } finally {
                    window.clearTimeout = clear;
                  }
                  document.documentElement.dataset["pdfReply"] = cleared ? "admitted" : "ignored";
                },
                { once: true },
              );
            });
          },
        });
      }
    };
  });
  await openAuthenticated(page);
  await openCase(page, `ATTACH-PDF-LATE-${randomUUID()}`);
  const panel = documentPanel(page);
  const { row } = await upload(
    page,
    panel,
    pdfName,
    pdfName,
    "application/pdf",
    syntheticPdf([redPage, bluePage]),
  );
  const controls = await previewDocument(row);
  await expect(controls.status).toHaveText("Page 1 of 2");
  await row.getByRole("button", { name: "Next page", exact: true }).click();
  const html = page.locator("html");
  await expect(html).toHaveAttribute("data-pdf-reply", "held");
  await expect(controls.status).toHaveText("Page 1 of 2");
  await expect(controls.iframe).toBeVisible();

  const withdrawal = await page.context().newPage();
  await withdrawal.goto(page.url());
  const withdrawalRow = withdrawal.locator(".attachment-list > li").filter({ hasText: pdfName });
  await withdrawalRow.getByRole("button", { name: "Withdraw attachment", exact: true }).click();
  await confirm(withdrawal, "/api/operation.execute");
  await withdrawal.close();
  await page
    .locator(".case-timeline-panel")
    .getByRole("tab", { name: "Attachments", exact: true })
    .click();
  await expect(panel.locator("iframe")).toHaveCount(0);
  await expect(row).toHaveCount(0);
  await page.evaluate(() => {
    document.dispatchEvent(new Event("release-pdf-reply"));
  });
  await expect(html).toHaveAttribute("data-pdf-reply", "ignored");
  await expect(panel.locator("iframe")).toHaveCount(0);
});

test("masks the case workspace when an admitted attachment list rejects the session @engine", async ({
  page,
}) => {
  await openAuthenticated(page);
  await openCase(page, `ATTACH-LIST-${randomUUID()}`);
  const entered = Promise.withResolvers<void>();
  const release = Promise.withResolvers<void>();
  const upstream = Promise.withResolvers<unknown>();
  await page.route("**/api/case.attachments", async (route) => {
    entered.resolve();
    await release.promise;
    const headers = await route.request().allHeaders();
    headers["cookie"] = (headers["cookie"] ?? "")
      .split(";")
      .filter((cookie) => !cookie.trim().startsWith("__Host-ClaimCoreSession="))
      .join(";");
    const response = await route.fetch({ headers, maxRedirects: 0 });
    const envelope: unknown = await response.json();
    upstream.resolve(envelope);
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
  expect(await upstream.promise).toMatchObject({ hostFailure: { code: "SESSION_REJECTED" } });
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
