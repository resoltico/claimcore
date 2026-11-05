import type { ServiceResponse } from "../src/protocol/types";
import { checkActionHierarchy } from "./action-hierarchy";
import { checkAuthorityTable } from "./authority-table-checks";
import { retainResponseBodies } from "./response-bodies";
import { createHash } from "node:crypto";
import { tmpdir } from "node:os";
import { mkdir, realpath, writeFile } from "node:fs/promises";
import { isAbsolute, relative, resolve, sep } from "node:path";
import { expect, type Page, type TestInfo, type Response, type Request } from "@playwright/test";
import { expectAccessible } from "./session-helpers";
import { noHorizontalOverflow } from "./layout-support";
import { accents, preferenceKey } from "../src/presentation/preferences";
import { termLabel } from "../src/presentation/terms";
import { isResponse } from "../src/protocol/client";
import type { MatrixRoute } from "./design-matrix-fixtures";
export type Dimension = {
  language: "en" | "lv" | "ar" | "en-XA";
  mode: "light" | "dark";
  accent: "green" | "blue";
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
        for (const accent of accents) {
          result.push({ width, language, mode: theme, accent });
        }
      }
    }
  }
  if (engine === "chromium") {
    result.push(
      { width: 320, language: "en-XA", mode: "light", accent: "green" as const },
      { width: 640, language: "en", mode: "light", accent: "green" as const, zoom: 2 },
    );
  }
  return result;
};
export const displayLocaleFor = (language: Dimension["language"]) =>
  (({ en: "en-GB", lv: "lv-LV", ar: "ar-EG", "en-XA": "en-GB" }) as const)[language];
export const configureDimension = async (page: Page, dimension: Dimension) => {
  await retainResponseBodies(page);
  await page.setViewportSize({ width: dimension.width, height: 900 });
  await page.emulateMedia({ reducedMotion: "reduce", colorScheme: dimension.mode });
  await page.addInitScript(
    ({ key, preferences }) => {
      localStorage.setItem(key, JSON.stringify(preferences));
    },
    {
      key: preferenceKey,
      preferences: {
        version: 3,
        language: dimension.language,
        displayLocale: displayLocaleFor(dimension.language),
        mode: dimension.mode,
        accent: dimension.accent,
      },
    },
  );
};
const ready = async (page: Page, route: MatrixRoute) => {
  await expect(page.locator(route.ready)).toBeVisible();
  const dialog = page.getByRole("dialog");
  if ((await dialog.count()) > 0) {
    await expect(dialog).toHaveCount(1);
    await expect(dialog.locator(".ui-dialog-title")).toBeVisible();
  } else {
    await expect(page.getByRole("heading", { level: 1 })).toHaveCount(1);
  }
  await expect(page.locator(".worklist-skeleton")).toHaveCount(0);
  await expect(page.locator("main [role=alert]")).toHaveCount(0);
  if (route.id.startsWith("case-") && route.id !== "case-erased") {
    await expect(page.locator(".case-fields").first()).toBeVisible();
  }
  if (route.id === "case-erased") {
    await expect(page.locator(".case-header-state .badge")).toBeVisible();
    await expect(page.locator(".case-fields")).toHaveCount(0);
  }
  if (route.id.startsWith("cases-")) {
    await expect(page.locator(".summary-band")).toBeVisible();
    await expect(page.locator(".summary-band[role=status], .summary-band[role=alert]")).toHaveCount(
      0,
    );
    await expect(page.locator('.view-tabs [role="tab"][aria-selected="true"]')).toHaveCount(1);
  }
  if (route.id.startsWith("case-")) {
    await expect(page.locator(".case-timeline > .timeline-entry").first()).toBeVisible();
    await expect(page.locator(".assigned-marker")).toHaveCount(
      route.id === "case-undecided" ? 1 : 0,
    );
  }
  if (route.id === "operation-result") {
    await expect(page.locator(".case-fields")).toBeVisible();
  }
  if (route.id === "authority-list") {
    await expect(page.locator('a[id^="actor-grants-"]').first()).toBeVisible();
  }
  if (route.id === "authority-detail") {
    await expect(page.locator('[role="dialog"] .actor-grant-items > li')).toHaveCount(1);
  }
  if ((await dialog.count()) > 0) {
    await expect
      .poll(() => dialog.evaluate((element) => element.contains(document.activeElement)))
      .toBe(true);
  } else {
    await expect(page.getByRole("heading", { level: 1 })).toBeFocused();
  }
};
const assertListPresentation = async (
  page: Page,
  payload: ServiceResponse<"case.list">,
  route: MatrixRoute,
  dimension: Dimension,
) => {
  if (payload.outcome.kind !== "SUCCEEDED") {
    return;
  }
  await expect(page.locator(".worklist-row")).toHaveCount(payload.outcome.data.items.length);
  for (const item of payload.outcome.data.items) {
    const row = page.locator(`.worklist-row:has(a[href="/cases/id/${item.caseId}"])`);
    const work = row.locator(".worklist-next-action");
    await expect(work.locator("[data-attention]")).toHaveCount(item.attention.length);
    expect(
      await work
        .locator("[data-attention]")
        .evaluateAll((elements) =>
          elements.map((element) => element.getAttribute("data-attention")),
        ),
    ).toEqual(item.attention);
    if (item.nextAction === null) {
      await expect(work.locator(":scope > bdi")).toHaveCount(0);
      await expect(work.locator("time")).toHaveCount(0);
    } else {
      await expect(work).toBeVisible();
      await expect(work.locator(":scope > bdi")).toHaveText(item.nextAction.action);
      const due = work.locator("time");
      await expect(due).toHaveAttribute("datetime", item.nextAction.due);
      await expect(due).toHaveAttribute("data-due", item.nextAction.dueState);
      const meaning = termLabel(
        {
          language: dimension.language,
          displayLocale: displayLocaleFor(dimension.language),
          mode: dimension.mode,
          accent: dimension.accent,
        },
        "due",
        item.nextAction.dueState,
      );
      await expect(due).toHaveAttribute("title", meaning);
      expect(await due.getAttribute("aria-label")).toContain(meaning);
    }
  }
  if (route.expectedReference === undefined) {
    return;
  }
  const assigned = payload.outcome.data.items.find(
    (item) => item.caseReference.toUpperCase() === route.expectedReference?.toUpperCase(),
  );
  expect(assigned, "The independently assigned matrix case must appear in Mine").toBeDefined();
  await expect(page.locator(`.worklist-row a[href="/cases/id/${assigned!.caseId}"]`)).toBeVisible();
};
const assertCasePresentation = async (
  page: Page,
  payload: ServiceResponse<"case.get">,
  route: MatrixRoute,
) => {
  if (payload.outcome.kind === "SUCCEEDED" && route.id.startsWith("case-")) {
    await expect(page.locator(".case-heading .eyebrow .identifier-value")).toHaveText(
      payload.outcome.data.caseReference,
    );
  }
};
const assertReadResponse = async (
  page: Page,
  reply: Response,
  route: MatrixRoute,
  dimension: Dimension,
) => {
  expect(reply.status()).toBe(200);
  const payload: unknown = await reply.json();
  if (route.endpoint === undefined || !isResponse(route.endpoint, payload)) {
    throw new Error("E2E_MATRIX_READ_REPLY_INVALID");
  }
  expect(payload.outcome.kind).toBe("SUCCEEDED");
  if (route.endpoint === "case.list" && isResponse("case.list", payload)) {
    await assertListPresentation(page, payload, route, dimension);
  }
  if (route.endpoint === "case.get" && isResponse("case.get", payload)) {
    await assertCasePresentation(page, payload, route);
  }
  if (
    route.endpoint === "authority.grants" &&
    isResponse("authority.grants", payload) &&
    payload.outcome.kind === "SUCCEEDED"
  ) {
    const { principal, actor } = payload.outcome.data.actor;
    const panel = page.getByRole("dialog");
    const details = panel.locator(".actor-record-details");
    await expect(details).not.toHaveAttribute("open");
    await details.locator(":scope > summary").click();
    await expect(details.locator("bdi")).toHaveText([
      principal.issuer,
      principal.subject,
      actor.actorId,
    ]);
    await expect(panel.locator(".ui-dialog-header > p")).toHaveText(
      termLabel(
        {
          language: dimension.language,
          displayLocale: displayLocaleFor(dimension.language),
          mode: dimension.mode,
          accent: dimension.accent,
        },
        "principalKind",
        principal.kind,
      ),
    );
    await details.locator(":scope > summary").click();
  }
  if (route.expectedInput !== undefined) {
    expect(reply.request().postDataJSON()).toEqual(route.expectedInput);
  }
};
const observeApiRequests = (page: Page) => {
  const requests: string[] = [];
  const observe = (request: Request) => {
    const path = new URL(request.url()).pathname;
    if (path.startsWith("/api/")) {
      requests.push(path);
    }
  };
  page.on("request", observe);
  return () => {
    page.off("request", observe);
    return requests;
  };
};
export const visitRoute = async (page: Page, route: MatrixRoute, dimension: Dimension) => {
  const apiRequests = observeApiRequests(page);
  const response =
    route.endpoint === undefined
      ? null
      : page.waitForResponse((value) => new URL(value.url()).pathname === `/api/${route.endpoint}`);
  await page.goto(route.path, { waitUntil: "domcontentloaded" });
  if (response !== null) {
    await assertReadResponse(page, await response, route, dimension);
  }
  if (dimension.zoom !== undefined) {
    await page.evaluate((zoom) => {
      document.documentElement.style.zoom = String(zoom);
    }, dimension.zoom);
  }
  await ready(page, route);
  if (route.id === "editor-work") {
    await page.locator("#case-work-update").click();
    await expect(page.getByRole("dialog").locator(".operation-form-card")).toBeVisible();
  }
  if (route.summaryExpanded === true) {
    await expect(page.locator(".summary-band")).toBeVisible();
    await expect(page.locator(".summary-band[role=status]")).toHaveCount(0);
    const disclosure = page.locator("details.summary-band");
    if (await disclosure.count()) {
      await disclosure.locator("summary").click();
    }
  }
  const observed = apiRequests();
  if (route.id === "not-found") {
    expect(observed).toEqual([]);
  }
  await expect(page.locator("html")).toHaveAttribute(
    "lang",
    dimension.language === "en-XA" ? "en" : dimension.language,
  );
  await expect(page.locator("html")).toHaveAttribute(
    "dir",
    dimension.language === "ar" ? "rtl" : "ltr",
  );
  await expect(page.locator("html")).toHaveAttribute("data-mode", dimension.mode);
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
  const expandedRowRect = (element: Element): DOMRect | null => {
    if (!element.matches("a.row-link") || getComputedStyle(element).position !== "static") {
      return null;
    }
    const row = element.closest(".worklist-row");
    if (row === null || getComputedStyle(row).position !== "relative") {
      return null;
    }
    const pseudo = getComputedStyle(element, "::after");
    const stretched =
      pseudo.content === '""' &&
      pseudo.position === "absolute" &&
      pseudo.pointerEvents !== "none" &&
      [pseudo.top, pseudo.right, pseudo.bottom, pseudo.left].every((value) => value === "0px");
    const width = Number.parseFloat(pseudo.width);
    const height = Number.parseFloat(pseudo.height);
    if (!stretched || !Number.isFinite(width) || !Number.isFinite(height)) {
      return null;
    }
    const box = row.getBoundingClientRect();
    const style = getComputedStyle(row);
    return new DOMRect(
      box.left + Number.parseFloat(style.borderLeftWidth) * scale,
      box.top + Number.parseFloat(style.borderTopWidth) * scale,
      width * scale,
      height * scale,
    );
  };
  const hitRects = (element: Element) => {
    const rects = [element.getBoundingClientRect()];
    // The native link's inset pseudo-element is the actual whole-card pointer target.
    const expanded = expandedRowRect(element);
    if (expanded !== null) {
      rects.push(expanded);
    }
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
        "button[type=submit], .case-heading button.primary, .case-actions button.primary, .case-action-bar .case-primary-action button, .worklist .section-heading button",
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
  const primaryButton = bar.locator(".case-primary-action button");
  const primary = (await primaryButton.count()) === 0 ? null : await primaryButton.boundingBox();
  if (primary !== null) {
    expect(primary.y + primary.height).toBeGreaterThanOrEqual(viewport - 24);
  }
  const more = bar.locator(".case-more-actions > summary");
  if ((await more.count()) > 0) {
    const bounds = await more.boundingBox();
    expect(bounds).not.toBeNull();
    expect(bounds!.y + bounds!.height).toBeGreaterThanOrEqual(viewport - 24);
    if (primary !== null) {
      expect(Math.abs(bounds!.y - primary.y)).toBeLessThanOrEqual(1);
    }
  }
  expect(primary !== null || (await more.count()) === 1).toBe(true);
  await compactMoreGeometry(page, bar);
  const scroll = await page.evaluate(() => window.scrollY);
  await page.evaluate(() => {
    window.scrollTo(0, document.documentElement.scrollHeight);
  });
  const footer = await page.locator(".case-timeline > .timeline-entry").last().boundingBox();
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
  for (const help of document.querySelectorAll(".case-fact-card .explain-fields")) {
    const walker = document.createTreeWalker(help, NodeFilter.SHOW_TEXT);
    for (let node = walker.nextNode(); node !== null; node = walker.nextNode()) {
      for (const word of (node.textContent ?? "").matchAll(/\S+/gu)) {
        const range = document.createRange();
        range.setStart(node, word.index);
        range.setEnd(node, word.index + word[0].length);
        if (lines(range) > 1) {
          failures.push({
            field: help.closest(".case-fact-card")!.querySelector("h3")!.textContent,
            kind: "help-word-wrap",
          });
        }
      }
    }
  }
  return failures;
};
const readableRows = async (page: Page, dimension: Dimension) => {
  await checkAuthorityTable(page, dimension.width >= 1024);
  const failures = await page.locator(".worklist-row:not(.erased-row)").evaluateAll(
    (rows, compact) =>
      rows.flatMap((row, index) => {
        const visible = (element: Element | null) =>
          element !== null &&
          element.getBoundingClientRect().width > 0 &&
          element.getBoundingClientRect().height > 0 &&
          getComputedStyle(element).visibility === "visible";
        const required = [
          ".reference",
          ".worklist-person > bdi",
          ".worklist-badges",
          ".worklist-money",
          ...(row.querySelector(
            ".worklist-next-action > bdi, .worklist-next-action > small, .worklist-next-action .case-attention",
          ) === null
            ? []
            : [".worklist-next-action"]),
          ...(compact
            ? []
            : [
                ".worklist-person > small",
                ".worklist-payable",
                ".worklist-responsible",
                ".worklist-changed",
              ]),
        ];
        const hidden = compact
          ? [
              ".worklist-person > small",
              ".worklist-payable",
              ".worklist-responsible",
              ".worklist-changed",
              ".case-stage-mini",
            ]
          : [];
        const party = row.querySelector(".worklist-person");
        return [
          ...required
            .filter((selector) => !visible(row.querySelector(selector)))
            .map((selector) => ({ row: index, selector, problem: "required-hidden" })),
          ...hidden
            .filter((selector) => visible(row.querySelector(selector)))
            .map((selector) => ({ row: index, selector, problem: "compact-secondary-visible" })),
          ...(party !== null && getComputedStyle(party).textAlign !== "start"
            ? [{ row: index, selector: ".worklist-person", problem: "party-alignment" }]
            : []),
          ...[...row.querySelectorAll(".worklist-person bdi")]
            .filter((element) => getComputedStyle(element).display !== "inline")
            .map(() => ({
              row: index,
              selector: ".worklist-person bdi",
              problem: "isolate-block",
            })),
        ];
      }),
    dimension.width < 1024,
  );
  expect(
    failures,
    "Core party values stay isolated; compact W4 and desktop retain their declared content",
  ).toEqual([]);
};
const viewTabGeometry = async (page: Page, dimension: Dimension) => {
  const tabs = page.locator('.view-tabs [role="tab"]');
  if (dimension.width >= 640 || (await tabs.count()) === 0) {
    return;
  }
  const tops = await tabs.evaluateAll((elements) =>
    elements.map((element) => Math.round(element.getBoundingClientRect().top)),
  );
  expect(
    [...new Set(tops)],
    "Compact core view tabs remain one horizontally scrollable line",
  ).toHaveLength(1);
};
const primaryByFrame: Readonly<Record<string, string | null>> = {
  "case-undecided": "DECIDE",
  "case-decided": "RECORD_PAYMENT",
  "case-paid": "CLOSE",
  "case-closed": null,
  "case-voided": null,
  "case-erased": null,
  "history-comparison": "CLOSE",
};
export const checkFrame = async (page: Page, dimension: Dimension, frame: string) => {
  await expectAccessible(page);
  await checkActionHierarchy(page, primaryByFrame[frame]);
  await compactActionGeometry(page, dimension);
  await readableRows(page, dimension);
  await viewTabGeometry(page, dimension);
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
    (dimension.language === "en" && dimension.width === 320 && dimension.mode === "light") ||
    (dimension.language === "ar" && dimension.width === 390 && dimension.mode === "light"));
export const expectedCaptures = (engine: string, dimension: Dimension): number =>
  engine === "chromium" && screenshotDimension(dimension)
    ? 15 +
      Number(dimension.language === "en" && dimension.width === 390 && dimension.mode === "light")
    : 0;
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
const capturesFrame = (frame: string, dimension: Dimension): boolean => {
  const baselinePair =
    frame === "cases-open-undecided" &&
    dimension.language === "en" &&
    dimension.width === 390 &&
    dimension.mode === "light";
  if (
    !baselinePair &&
    ![
      "cases-all",
      "cases-mine",
      "cases-summary",
      "case-decided",
      "case-voided",
      "case-erased",
      "editor-decide",
      "authority-list",
      "authority-detail",
      "authority-history",
      "not-found",
      "recovery",
      "editor-work",
      "paid-correction-review",
      "history-comparison",
    ].includes(frame)
  ) {
    return false;
  }
  return true;
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
  if (!capturesFrame(frame, dimension)) {
    return null;
  }
  const root = await matrixEvidenceDirectory();
  const path = resolve(
    root,
    `${dimension.language}-${dimension.width}-${dimension.accent}-${dimension.mode}-${frame}.png`,
  );
  await page.screenshot({
    path,
    fullPage: frame !== "paid-correction-review",
    animations: "disabled",
  });
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
