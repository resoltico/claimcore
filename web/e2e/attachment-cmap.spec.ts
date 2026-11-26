import { randomUUID } from "node:crypto";
import { expect, test } from "./fixtures";
import { jpeg2000Pdf, syntheticPdf } from "./attachment-pdf-fixtures";
import { uploadAttachment } from "./attachment-workflow";
import { openCase } from "./case-workflow";
import { openAuthenticated } from "./session-helpers";

const japanese = () =>
  syntheticPdf(["0 g BT /F1 42 Tf 25 90 Td <65E5> Tj 100 0 Td <672C> Tj ET"], {
    dictionary: "<< /Font << /F1 5 0 R >> >>",
    objects: [
      "<< /Type /Font /Subtype /Type0 /BaseFont /HeiseiKakuGo-W5 " +
        "/Encoding /UniJIS-UCS2-H /DescendantFonts [6 0 R] >>",
      "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /HeiseiKakuGo-W5 " +
        "/CIDSystemInfo << /Registry (Adobe) /Ordering (Japan1) /Supplement 2 >> " +
        "/DW 1000 /FontDescriptor 7 0 R >>",
      "<< /Type /FontDescriptor /FontName /HeiseiKakuGo-W5 /Flags 4 " +
        "/FontBBox [-100 -250 1000 900] /ItalicAngle 0 /Ascent 880 /Descent -120 /CapHeight 700 /StemV 85 >>",
    ],
  });

test("renders Japanese from a relayed CMap and ignores names outside the published map", async ({
  page,
}) => {
  test.setTimeout(90_000);
  await page.addInitScript(() => {
    if (location.pathname !== "/attachment-preview/viewer" || window === parent) {
      return;
    }
    window.addEventListener("message", (event: MessageEvent<unknown>) => {
      const [port] = event.ports;
      if (event.source !== parent || event.data !== "claimcore-attachment-preview" || !port) {
        return;
      }
      const resourceId = 9001;
      port.addEventListener("message", ({ data }: MessageEvent<unknown>) => {
        if (
          typeof data === "object" &&
          data !== null &&
          "resourceId" in data &&
          data.resourceId === resourceId
        ) {
          document.documentElement.dataset["unexpectedCmap"] = "answered";
        }
      });
      port.postMessage({
        kind: "resource",
        resourceId,
        resourceType: "cmaps",
        name: "not-in-the-inventory.bcmap",
      });
      document.documentElement.dataset["unlistedCmapProbe"] = "sent";
    });
  });
  await openAuthenticated(page);
  await openCase(page, `ATTACH-CMAP-${randomUUID().slice(0, 8)}`);

  const attachments = page.getByRole("region", { name: "Attachments", exact: true });
  const name = "synthetic-japanese.pdf";
  const { row } = await uploadAttachment(
    page,
    attachments,
    name,
    name,
    "application/pdf",
    japanese(),
  );

  const unexpected: string[] = [];
  page.on("request", (request) => {
    if (request.url().includes("not-in-the-inventory")) {
      unexpected.push(request.url());
    }
  });
  const cmap = page.waitForResponse(
    (response) =>
      /\/assets\/pdf-UniJIS-UCS2-H-[a-f0-9]+\.bcmap$/u.test(new URL(response.url()).pathname) &&
      response.ok(),
  );
  await row.getByRole("button", { name: "Preview", exact: true }).click();
  expect((await (await cmap).body()).length).toBeGreaterThan(0);
  await expect(row.getByRole("status").filter({ hasText: "Page 1 of 1" })).toBeVisible();

  const frame = row.frameLocator('iframe[title="Preview"]');
  await expect(frame.locator("html")).toHaveAttribute("data-unlisted-cmap-probe", "sent");
  await expect(frame.locator("html")).not.toHaveAttribute("data-unexpected-cmap", "answered");
  expect(unexpected).toEqual([]);
  const glyphs = await frame.locator("canvas").evaluate((canvas) => {
    if (!(canvas instanceof HTMLCanvasElement)) {
      throw new Error("The PDF viewer did not produce a canvas.");
    }
    const context = canvas.getContext("2d")!;
    const { width, height } = context.canvas;
    const { data } = context.getImageData(0, 0, width, height);
    const columns = Array.from({ length: width }, () => 0);
    for (let index = 0; index < data.length; index += 4) {
      if (data[index]! < 90 && data[index + 3]! > 180) {
        const column = (index / 4) % width;
        columns[column]! += 1;
      }
    }
    const first = columns.slice(0, Math.round(width / 2)).filter((value) => value > 0);
    const second = columns.slice(Math.round(width / 2)).filter((value) => value > 0);
    return {
      first: first.reduce((total, value) => total + value, 0),
      second: second.reduce((total, value) => total + value, 0),
      distinct: first.length !== second.length || first.some((value, i) => value !== second[i]),
    };
  });
  expect(glyphs.first).toBeGreaterThan(20);
  expect(glyphs.second).toBeGreaterThan(20);
  expect(glyphs.distinct).toBe(true);
});

test("decodes JPEG 2000 with WASM relayed by the workspace @engine", async ({ page }) => {
  await openAuthenticated(page);
  await openCase(page, `ATTACH-JPX-${randomUUID().slice(0, 8)}`);
  const name = "synthetic-jpeg2000.pdf";
  const { row } = await uploadAttachment(
    page,
    page.getByRole("region", { name: "Attachments", exact: true }),
    name,
    name,
    "application/pdf",
    jpeg2000Pdf(),
  );

  const decoder = page.waitForResponse(
    (response) =>
      /\/assets\/pdf-openjpeg-[a-f0-9]+\.wasm$/u.test(new URL(response.url()).pathname) &&
      response.ok() &&
      response.request().frame() === page.mainFrame(),
  );
  await row.getByRole("button", { name: "Preview", exact: true }).click();
  const wasm = await (await decoder).body();
  expect([...wasm.subarray(0, 4)]).toEqual([0x00, 0x61, 0x73, 0x6d]);
  await expect(row.getByRole("status").filter({ hasText: "Page 1 of 1" })).toBeVisible();

  const canvas = row.frameLocator('iframe[title="Preview"]').locator("canvas");
  const [red, green, blue, alpha] = await canvas.evaluate((element) => {
    if (!(element instanceof HTMLCanvasElement)) {
      throw new Error("The PDF viewer did not produce a canvas.");
    }
    const context = element.getContext("2d")!;
    const x = Math.floor(element.width / 2);
    const y = Math.floor(element.height / 2);
    return [...context.getImageData(x, y, 1, 1).data];
  });
  expect(red).toBeGreaterThan(240);
  expect(green).toBeLessThan(16);
  expect(blue).toBeLessThan(16);
  expect(alpha).toBe(255);
});
