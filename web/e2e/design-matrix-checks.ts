import { checkActionHierarchy } from "./action-hierarchy";
import { retainResponseBodies } from "./response-bodies";
import { createHash } from "node:crypto";
import { tmpdir } from "node:os";
import { mkdir, realpath, writeFile } from "node:fs/promises";
import { isAbsolute, relative, resolve, sep } from "node:path";
import { expect, type Page, type TestInfo, type Response } from "@playwright/test";
import { expectAccessible } from "./session-helpers";
import { noHorizontalOverflow } from "./layout-support";
import { preferenceKey } from "../src/presentation/preferences";
import { isResponse } from "../src/protocol/client";
import type { MatrixRoute } from "./design-matrix-fixtures";
export type Dimension = {
  language: "en" | "lv" | "ar" | "en-XA";
  theme: "light" | "dark";
  width: number;
  zoom?: number;
};
export const dimensions = (engine: string): Dimension[] => {
  const result: Dimension[] = [];
  const widths = engine === "chromium" ? [320, 390, 768, 1280] : [320, 1280];
  const languages = engine === "chromium" ? (["en", "lv", "ar"] as const) : (["en", "ar"] as const);
  const themes = engine === "chromium" ? (["light", "dark"] as const) : (["light"] as const);
  for (const width of widths) {
    for (const language of languages) {
      for (const theme of themes) {
        result.push({ width, language, theme });
      }
    }
  }
  if (engine === "chromium") {
    result.push(
      { width: 320, language: "en-XA", theme: "light" },
      { width: 640, language: "en", theme: "light", zoom: 2 },
    );
  }
  return result;
};
export const configureDimension = async (page: Page, dimension: Dimension) => {
  await retainResponseBodies(page);
  await page.setViewportSize({ width: dimension.width, height: 900 });
  await page.emulateMedia({ reducedMotion: "reduce", colorScheme: dimension.theme });
  await page.addInitScript(
    ({ key, preferences }) => {
      localStorage.setItem(key, JSON.stringify(preferences));
    },
    {
      key: preferenceKey,
      preferences: {
        version: 2,
        language: dimension.language,
        displayLocale: { en: "en-GB", lv: "lv-LV", ar: "ar-EG", "en-XA": "en-GB" }[
          dimension.language
        ],
        theme: dimension.theme.toUpperCase(),
      },
    },
  );
};
const ready = async (page: Page, route: MatrixRoute) => {
  await expect(page.locator(route.ready)).toBeVisible();
  await expect(page.locator("h1")).toHaveCount(1);
  await expect(page.locator(".worklist-skeleton")).toHaveCount(0);
  await expect(page.locator("main [role=alert]")).toHaveCount(0);
  if (route.id.startsWith("case-") && route.id !== "case-erased") {
    await expect(page.locator(".case-fields").first()).toBeVisible();
  }
  if (route.id === "case-erased") {
    await expect(page.locator(".case-lifecycle-erased")).toBeVisible();
    await expect(page.locator(".case-fields")).toHaveCount(0);
  }
  if (route.id.startsWith("cases-")) {
    await expect(page.locator('.saved-views button[aria-pressed="true"]')).toHaveCount(
      route.filtered === true ? 0 : 1,
    );
  }
  if (route.id.startsWith("case-")) {
    await expect(page.locator(".history-list > li").first()).toBeVisible();
  }
  if (route.id === "operation-result") {
    await expect(page.locator(".case-fields")).toBeVisible();
  }
  if (route.id === "authority-list") {
    await expect(page.locator('button[id^="actor-grants-"]').first()).toBeVisible();
  }
  if (route.id === "authority-detail") {
    await expect(page.locator('[aria-labelledby="actor-grants-title"] li')).toHaveCount(1);
  }
  await expect(page.locator("h1")).toBeFocused();
};
const assertReadResponse = async (page: Page, reply: Response, route: MatrixRoute) => {
  expect(reply.status()).toBe(200);
  const payload: unknown = await reply.json();
  if (route.endpoint === undefined || !isResponse(route.endpoint, payload)) {
    throw new Error("E2E_MATRIX_READ_REPLY_INVALID");
  }
  expect(payload.outcome.kind).toBe("SUCCEEDED");
  if (
    route.endpoint === "authority.grants" &&
    isResponse("authority.grants", payload) &&
    payload.outcome.kind === "SUCCEEDED"
  ) {
    const { principal } = payload.outcome.data.actor;
    await expect(page.locator('[name="target.subject"]')).toHaveValue(principal.subject);
    await expect(page.locator('[name="target.issuer"]')).toHaveValue(principal.issuer);
    await expect(page.locator(`[name="target.kind"][value="${principal.kind}"]`)).toBeChecked();
    await expect(page.locator('[name="target.subject"]')).toBeDisabled();
    await expect(page.locator("#act-picker")).toHaveValue("GRANT");
  }
  if (route.expectedInput !== undefined) {
    expect(reply.request().postDataJSON()).toEqual(route.expectedInput);
  }
};
export const visitRoute = async (page: Page, route: MatrixRoute, dimension: Dimension) => {
  const response =
    route.endpoint === undefined
      ? null
      : page.waitForResponse((value) => new URL(value.url()).pathname === `/api/${route.endpoint}`);
  await page.goto(route.path, { waitUntil: "domcontentloaded" });
  if (response !== null) {
    await assertReadResponse(page, await response, route);
  }
  if (dimension.zoom !== undefined) {
    await page.evaluate((zoom) => {
      document.documentElement.style.zoom = String(zoom);
    }, dimension.zoom);
  }
  await ready(page, route);
  await expect(page.locator("html")).toHaveAttribute(
    "lang",
    dimension.language === "en-XA" ? "en" : dimension.language,
  );
  await expect(page.locator("html")).toHaveAttribute(
    "dir",
    dimension.language === "ar" ? "rtl" : "ltr",
  );
  await expect(page.locator("html")).toHaveAttribute("data-theme", dimension.theme);
  if (dimension.zoom !== undefined) {
    expect(
      await page.evaluate(
        () => window.innerWidth / Number(getComputedStyle(document.documentElement).zoom),
      ),
    ).toBe(320);
  }
};
const clippedText = () =>
  [...document.querySelectorAll("button, summary, h1, h2, h3, label")]
    .filter((element) => {
      const style = getComputedStyle(element);
      const rect = element.getBoundingClientRect();
      return (
        rect.width > 1 &&
        rect.height > 1 &&
        style.visibility !== "hidden" &&
        style.clipPath === "none" &&
        ["hidden", "clip"].includes(style.overflowX) &&
        element.scrollWidth > element.clientWidth + 1
      );
    })
    .map((element) => element.tagName);
const smallTargets = () => {
  const zoom = Number(getComputedStyle(document.documentElement).zoom);
  const scale = Number.isFinite(zoom) && zoom > 0 ? zoom : 1;
  const visible = (element: Element) => {
    const rect = element.getBoundingClientRect();
    return (
      rect.width > 0 &&
      rect.height > 0 &&
      getComputedStyle(element).visibility !== "hidden" &&
      element.closest("[inert], [aria-hidden=true]") === null &&
      !element.matches(":disabled, [aria-disabled=true]")
    );
  };
  const hitRects = (element: Element) => {
    const rects = [element.getBoundingClientRect()];
    if (element instanceof HTMLInputElement && ["checkbox", "radio"].includes(element.type)) {
      for (const label of element.labels ?? []) {
        const box = label.getBoundingClientRect();
        if (box.width > 0 && box.height > 0) {
          rects.push(box);
        }
      }
    }
    return rects;
  };
  return [
    ...document.querySelectorAll(
      "button, a[href], input, select, textarea, summary, [role=button]",
    ),
  ]
    .filter(visible)
    .flatMap((element) => {
      const rects = hitRects(element);
      const width =
        (Math.max(...rects.map((box) => box.right)) - Math.min(...rects.map((box) => box.left))) /
        scale;
      const height =
        (Math.max(...rects.map((box) => box.bottom)) - Math.min(...rects.map((box) => box.top))) /
        scale;
      const minimum = element.matches(
        "button[type=submit], .case-actions button.primary, .case-action-bar .case-primary-action button, .worklist .section-heading button",
      )
        ? 44
        : 24;
      return width + 0.01 < minimum || height + 0.01 < minimum
        ? [{ tag: element.tagName, width, height, minimum }]
        : [];
    });
};
const compactMoreGeometry = async (page: Page, bar: ReturnType<Page["locator"]>) => {
  const more = bar.locator(".case-more-actions > summary");
  if ((await more.count()) === 0) {
    return;
  }
  const focus = await page.locator(":focus").elementHandle();
  await more.click();
  const popup = bar.locator(".case-more-popup");
  const bounds = await popup.boundingBox();
  const boundary = await bar.boundingBox();
  const viewport = await page.evaluate(() => ({
    width: window.innerWidth,
    height: window.innerHeight,
  }));
  expect(bounds).not.toBeNull();
  expect(boundary).not.toBeNull();
  expect(bounds!.x).toBeGreaterThanOrEqual(0);
  expect(bounds!.y).toBeGreaterThanOrEqual(0);
  expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(viewport.width);
  expect(bounds!.y + bounds!.height).toBeLessThanOrEqual(boundary!.y);
  const last = popup.locator("button").last();
  await last.scrollIntoViewIfNeeded();
  const target = await last.boundingBox();
  expect(target).not.toBeNull();
  expect(target!.y).toBeGreaterThanOrEqual(bounds!.y);
  expect(target!.y + target!.height).toBeLessThanOrEqual(bounds!.y + bounds!.height);
  await more.click();
  await focus?.focus();
};
const compactActionGeometry = async (page: Page, dimension: Dimension) => {
  const bar = page.locator(".case-action-bar");
  if (dimension.width >= 640 || (await bar.count()) === 0) {
    return;
  }
  const viewport = await page.evaluate(() => window.innerHeight);
  const primary = await bar.locator(".case-primary-action button").boundingBox();
  expect(primary).not.toBeNull();
  expect(primary!.y + primary!.height).toBeGreaterThanOrEqual(viewport - 24);
  const more = bar.locator(".case-more-actions > summary");
  if ((await more.count()) > 0) {
    const bounds = await more.boundingBox();
    expect(bounds).not.toBeNull();
    expect(bounds!.y + bounds!.height).toBeGreaterThanOrEqual(viewport - 24);
    expect(Math.abs(bounds!.y - primary!.y)).toBeLessThanOrEqual(1);
  }
  await compactMoreGeometry(page, bar);
  const scroll = await page.evaluate(() => window.scrollY);
  await page.evaluate(() => {
    window.scrollTo(0, document.documentElement.scrollHeight);
  });
  const footer = await page.locator(".case-history > button").last().boundingBox();
  const boundary = await bar.boundingBox();
  if (footer !== null && boundary !== null) {
    expect(footer.y + footer.height).toBeLessThanOrEqual(boundary.y + 1);
  }
  await page.evaluate((value) => {
    window.scrollTo(0, value);
  }, scroll);
};
const factCardReadability = () => {
  const lines = (range: Range) =>
    new Set([...range.getClientRects()].map((rect) => Math.round(rect.top * 2) / 2)).size;
  const failures: { field: string; kind: string }[] = [];
  for (const date of document.querySelectorAll(
    '.case-fact-card [data-field-name$="Date"] dd bdi',
  )) {
    const range = document.createRange();
    range.selectNodeContents(date);
    if (lines(range) > 1) {
      failures.push({
        field: date.closest<HTMLElement>("[data-field-name]")!.dataset["fieldName"]!,
        kind: "date-wrap",
      });
    }
  }
  for (const summary of document.querySelectorAll(".case-fact-card summary")) {
    const walker = document.createTreeWalker(summary, NodeFilter.SHOW_TEXT);
    for (let node = walker.nextNode(); node !== null; node = walker.nextNode()) {
      for (const word of (node.textContent ?? "").matchAll(/\S+/gu)) {
        const range = document.createRange();
        range.setStart(node, word.index);
        range.setEnd(node, word.index + word[0].length);
        if (lines(range) > 1) {
          failures.push({
            field: summary.closest<HTMLElement>("[data-field-name]")!.dataset["fieldName"]!,
            kind: "help-word-wrap",
          });
        }
      }
    }
  }
  return failures;
};
export const checkFrame = async (page: Page, dimension: Dimension) => {
  await expectAccessible(page);
  await checkActionHierarchy(page);
  await compactActionGeometry(page, dimension);
  expect(await page.evaluate(factCardReadability)).toEqual([]);
  const targets = await page.evaluate(smallTargets);
  const tags = new Set(["BUTTON", "A", "INPUT", "SELECT", "TEXTAREA", "SUMMARY", "DIV", "SPAN"]);
  const finiteDimension = (value: number) =>
    Number.isFinite(value) && value >= 0 && value <= 9999.99;
  const geometry = targets
    .filter(
      ({ width, height, minimum }) =>
        finiteDimension(width) && finiteDimension(height) && (minimum === 24 || minimum === 44),
    )
    .slice(0, 5)
    .map(
      ({ tag, width, height, minimum }) =>
        `E2E_TARGET_GEOMETRY tag=${tags.has(tag) ? tag : "OTHER"} width=${width.toFixed(2)} height=${height.toFixed(2)} min=${minimum}`,
    );
  expect(targets, geometry.join("\n") || "E2E_TARGET_GEOMETRY_UNAVAILABLE").toEqual([]);
  await noHorizontalOverflow(page, dimension.zoom === undefined ? "narrow" : "zoom");
  if (dimension.language === "en-XA" || dimension.zoom !== undefined) {
    expect(await page.evaluate(clippedText)).toEqual([]);
  }
};
const screenshotDimension = (dimension: Dimension): boolean =>
  dimension.zoom === undefined &&
  ((dimension.language === "en" && [390, 1280].includes(dimension.width)) ||
    (dimension.language === "ar" && dimension.width === 390 && dimension.theme === "light"));
export const expectedCaptures = (engine: string, dimension: Dimension): number =>
  engine === "chromium" && screenshotDimension(dimension) ? 21 : 0;
export const matrixEvidenceDirectory = async (): Promise<string> => {
  const configured =
    process.env["CLAIMCORE_DESIGN_SCREENSHOT_DIRECTORY"] ??
    resolve(
      tmpdir(),
      "claimcore-design-matrix-evidence",
      `${process.env["CLAIMCORE_WEB_E2E_ENGINE"] ?? "chromium"}-${process.pid}`,
    );
  if (!isAbsolute(configured)) {
    throw new Error(
      "CLAIMCORE_DESIGN_SCREENSHOT_DIRECTORY must be absolute and outside the repository and published fixture.",
    );
  }
  await mkdir(configured, { recursive: true, mode: 0o700 });
  const root = await realpath(configured);
  const parents = [resolve(import.meta.dirname, "../..")];
  const fixture = process.env["CLAIMCORE_WEB_E2E_REDIRECT_STATE_DIR"];
  if (fixture !== undefined) {
    parents.push(fixture);
  }
  for (const parent of parents) {
    const path = relative(await realpath(parent), root);
    if (path === "" || (path !== ".." && !path.startsWith(`..${sep}`) && !isAbsolute(path))) {
      throw new Error("E2E_MATRIX_EVIDENCE_MUST_SURVIVE_REPOSITORY_AND_FIXTURE_CLEANUP");
    }
  }
  return root;
};
export const captureFrame = async (
  page: Page,
  info: TestInfo,
  dimension: Dimension,
  frame: string,
): Promise<string | null> => {
  if (info.project.name !== "chromium" || !screenshotDimension(dimension)) {
    return null;
  }
  const root = await matrixEvidenceDirectory();
  const path = resolve(
    root,
    `${dimension.language}-${dimension.width}-${dimension.theme}-${frame}.png`,
  );
  await page.screenshot({ path, fullPage: true, animations: "disabled" });
  await info.attach(`design-${frame}`, { path, contentType: "image/png" });
  return path;
};
export const writeMatrixReport = async (
  info: TestInfo,
  report: Readonly<Record<string, unknown>>,
  name = "design-matrix-counts",
) => {
  const root = await matrixEvidenceDirectory();
  const id = createHash("sha256").update(info.testId).digest("hex").slice(0, 16);
  const path = resolve(root, `${info.project.name}-${name}-${id}.json`);
  await writeFile(path, `${JSON.stringify(report, null, 2)}\n`, { mode: 0o600 });
  await info.attach(name, { path, contentType: "application/json" });
};
