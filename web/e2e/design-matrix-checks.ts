import { expectNoInlineStyles } from "./inline-styles";
import { geometryFailures } from "./design-geometry";
import { caseLinkId } from "../src/routing/coreLinks";
import {
  type Screen,
  type ResponseFor,
  presentationDisplayOptions,
  presentationDefaults,
} from "@contracts/client";

import { createPresenter } from "../src/presentation/presenter";
import { termLabel } from "../src/presentation/terms";
import { screenshotDimension, capturesFrame, type Dimension } from "./design-matrix-plan";
import type { ServiceResponse } from "../src/protocol/types";
import { checkActionHierarchy } from "./action-hierarchy";
import { retainResponseBodies } from "./response-bodies";
import { tmpdir } from "node:os";
import { mkdir, realpath } from "node:fs/promises";
import { isAbsolute, relative, resolve, sep } from "node:path";
import { expect, type Page, type TestInfo, type Response, type Request } from "@playwright/test";
import { expectAccessible } from "./session-helpers";
import { noHorizontalOverflow } from "./layout-support";
import { preferenceKey } from "../src/presentation/preferences";
import {} from "./localization-support";
import type { MatrixRoute } from "./design-matrix-fixtures";
export const displayLocaleFor = (language: Dimension["language"]) =>
  presentationDisplayOptions.find((option) => option.language === language)?.value ??
  presentationDefaults.displayLocale;
const assertQueueOrReport = async (page: Page, route: MatrixRoute) => {
  if (route.id === "cases-summary") {
    await expect(page.locator(".summary-band")).toBeVisible();
    await expect(page.locator(".summary-band[role=status], .summary-band[role=alert]")).toHaveCount(
      0,
    );
  }
  if (route.id.startsWith("cases-") && route.id !== "cases-summary") {
    await expect(page.locator(".summary-band")).toHaveCount(0);
    await expect(page.locator('.view-tabs [role="tab"][aria-selected="true"]')).toHaveCount(1);
  }
};
export const configureDimension = async (page: Page, dimension: Dimension) => {
  await retainResponseBodies(page);
  await page.setViewportSize({
    // Reflow uses the CSS viewport produced by browser zoom. Root CSS zoom instead
    // mixes scaled client rects with unscaled overlay offsets and does not resize media queries.
    width: dimension.width / (dimension.reflowScale ?? 1),
    height: 900 / (dimension.reflowScale ?? 1),
  });
  await page.emulateMedia({
    reducedMotion: "reduce",
    colorScheme: dimension.mode,
  });
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
    await expect(
      page.getByRole("heading", {
        level: 1,
      }),
    ).toHaveCount(1);
  }
  await expect(page.locator(".worklist-skeleton")).toHaveCount(0);
  const alerts = page.getByRole("main").getByRole("alert");
  await expect(alerts, JSON.stringify(await alerts.allTextContents())).toHaveCount(0);
  if (route.id.startsWith("case-") && route.id !== "case-erased") {
    await expect(page.locator(".entries-view [data-entry='policyNumber']").first()).toBeVisible();
  }
  if (route.id === "case-erased") {
    await expect(page.locator('.region-notice[data-tone="FINAL"]')).toBeVisible();
    await expect(page.locator(".entries-view [data-entry='policyNumber']")).toHaveCount(0);
  }
  await assertQueueOrReport(page, route);
  if (route.id.startsWith("case-")) {
    await expect(page.locator(".case-timeline-panel .region-timeline > li").first()).toBeVisible();
  }
  if (route.id === "operation-result") {
    await expect(page.locator(".receipt .region-notice").first()).toBeVisible();
  }
  if (route.id === "people-list") {
    await expect(page.locator('a[id^="actor-grants-"]').first()).toBeVisible();
  }
  if (route.id === "people-detail") {
    await expect(page.locator('[role="dialog"] .table-view tbody > tr')).toHaveCount(1);
  }
  if ((await dialog.count()) > 0) {
    await expect
      .poll(() => dialog.evaluate((element) => element.contains(document.activeElement)))
      .toBe(true);
  } else {
    await expect(
      page.getByRole("heading", {
        level: 1,
      }),
    ).toBeFocused();
  }
};
const assertListPresentation = async (
  page: Page,
  payload: ServiceResponse<"case.list">,
  route: MatrixRoute,
  _dimension: Dimension,
) => {
  if (payload.outcome.kind !== "SUCCEEDED") {
    return;
  }
  const results = page.locator(
    '.worklist .view-tab-panel > .screen-view > [data-region="RESULTS"]',
  );
  const rows = results.locator(".table-view tbody > tr:not(.table-group)");
  const table = payload.outcome.document.screen.regions.find(
    (region) => region.role === "RESULTS",
  )?.content;
  if (table?.kind !== "TABLE") {
    throw new Error("The case page must supply its table view.");
  }
  await expect(rows).toHaveCount(table.rows.length);
  for (const [index, item] of table.rows.entries()) {
    const caseId = item.link === undefined ? undefined : caseLinkId(item.link);
    if (caseId === undefined) {
      throw new Error("Case query required");
    }
    const row = rows.nth(index);
    await expect(row.locator(`a[href="/cases/id/${caseId}"]`)).toBeVisible();
    const material = table.columns.filter(({ prominence }) => prominence !== "DETAIL");
    await expect(row.locator("td[data-column]")).toHaveCount(material.length);
    for (const column of table.columns.filter(({ prominence }) => prominence === "DETAIL")) {
      await expect(row.locator(`td[data-column="${column.key}"]`)).toHaveCount(0);
    }
  }
  if (route.expectedReference === undefined) {
    return;
  }
  const assigned = table.rows.find((row) =>
    row.values.some(
      (value) =>
        value.kind === "REFERENCE" &&
        value.value.toUpperCase() === route.expectedReference?.toUpperCase(),
    ),
  );
  expect(assigned, "The independently assigned matrix case must appear in Mine").toBeDefined();
  await expect(rows.locator(`a[href="/cases/id/${assigned!.key}"]`)).toBeVisible();
};
const principalKindFact = (screen: Screen) => {
  const value = screen.regions
    .flatMap((region) => (region.content.kind === "ENTRIES" ? region.content.entries : []))
    .find((entry) => entry.key === "principalKind")?.value;
  if (value?.kind !== "CODE" || (value.token !== "HUMAN" && value.token !== "SERVICE")) {
    throw new Error("Core principal kind required");
  }
  return value.token;
};
const assertCasePresentation = async (
  page: Page,
  payload: ServiceResponse<"case.get">,
  route: MatrixRoute,
) => {
  if (payload.outcome.kind === "SUCCEEDED" && route.id.startsWith("case-")) {
    const reference = payload.outcome.document.screen.regions
      .flatMap((region) =>
        region.role === "IDENTITY" && region.content.kind === "ENTRIES"
          ? region.content.entries
          : [],
      )
      .find((entry) => entry.key === "caseReference")?.value;
    if (reference?.kind !== "REFERENCE") {
      throw new Error("The Core case document must supply its retained reference.");
    }
    await expect(page.locator("#case-detail-title .identifier-value")).toHaveText(reference.value);
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
  if (route.endpoint === undefined) {
    throw new Error("E2E_MATRIX_ENDPOINT_MISSING");
  }
  const endpointReply = payload as Extract<
    ResponseFor<Exclude<typeof route.endpoint, undefined>, "HUMAN">,
    { readonly representation: "HUMAN" }
  >;
  if (endpointReply.representation !== "HUMAN") {
    throw new Error("E2E_MATRIX_READ_REPLY_INVALID");
  }
  expect(endpointReply.outcome.kind).toBe(
    route.endpoint === "operation.get" ? "ACCEPTED" : "SUCCEEDED",
  );
  if (route.endpoint === "case.list") {
    const caseListReply = payload as ResponseFor<"case.list", "HUMAN">;
    await assertListPresentation(page, caseListReply, route, dimension);
  }
  if (route.endpoint === "case.get") {
    const caseGetReply = payload as ResponseFor<"case.get", "HUMAN">;
    await assertCasePresentation(page, caseGetReply, route);
  }
  const peopleGrantsReply = payload as ResponseFor<"people.grants", "HUMAN">;
  if (route.endpoint === "people.grants" && peopleGrantsReply.outcome.kind === "SUCCEEDED") {
    const principalKind = principalKindFact(peopleGrantsReply.outcome.document.screen);
    const panel = page.getByRole("dialog");
    await expect(panel.locator('[data-entry="principalKind"] dd')).toHaveText(
      termLabel(
        {
          language: dimension.language,
          displayLocale: displayLocaleFor(dimension.language),
          mode: dimension.mode,
          accent: dimension.accent,
        },
        "principalKind",
        principalKind,
      ),
    );
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
const settleCaseList = async (page: Page, dimension: Dimension) => {
  const p = createPresenter({
    language: dimension.language,
    displayLocale: displayLocaleFor(dimension.language),
    mode: dimension.mode,
    accent: dimension.accent,
  });
  const list = page.locator(".worklist");
  const pressure = list.getByRole("alert").filter({
    hasText: p.text("host.RATE_LIMITED"),
  });
  for (let attempt = 0; attempt < 3; attempt += 1) {
    await expect(list).toBeVisible();
    await expect(list).toHaveAttribute("aria-busy", "false");
    await expect(list.locator('[role="status"]:not(.region-notice)')).toHaveCount(0);
    if (!(await pressure.first().isVisible())) {
      return;
    }
    // Each independent read panel must settle; retry one known pressure refusal at a time.
    await pressure
      .first()
      .getByRole("button", {
        name: p.text("ui.tryAgain"),
        exact: true,
      })
      .click();
  }
  await expect(pressure).toHaveCount(0);
};
const openReadRoute = async (page: Page, route: MatrixRoute, dimension: Dimension) => {
  const listRead = route.endpoint === "case.list";
  const listResponses: Response[] = [];
  const collect = (reply: Response) => {
    if (new URL(reply.url()).pathname === "/api/case.list") {
      listResponses.push(reply);
    }
  };
  if (listRead) {
    page.on("response", collect);
  }
  const response =
    route.endpoint === undefined || listRead
      ? null
      : page.waitForResponse(
          (value) =>
            new URL(value.url()).pathname === `/api/${route.endpoint}` && value.status() === 200,
        );
  await page.goto(route.path, {
    waitUntil: "domcontentloaded",
  });
  if (listRead) {
    try {
      await settleCaseList(page, dimension);
      const completed = listResponses.findLast((reply) => reply.status() === 200);
      expect(completed, "The current case table requires a successful Core read").toBeDefined();
      await assertReadResponse(page, completed!, route, dimension);
    } finally {
      page.off("response", collect);
    }
  } else if (response !== null) {
    await assertReadResponse(page, await response, route, dimension);
  }
};
export const visitRoute = async (page: Page, route: MatrixRoute, dimension: Dimension) => {
  const apiRequests = observeApiRequests(page);
  await openReadRoute(page, route, dimension);
  await ready(page, route);
  if (route.id === "editor-work") {
    expect(await geometryFailures(page)).not.toContain("G1");
    await page.locator("#case-work-summary").click();
    const summary = page.getByRole("dialog").locator('textarea[name="field:handlingSummary"]');
    await summary.scrollIntoViewIfNeeded();
    await expect(summary).toBeInViewport({ ratio: 1 });
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
  if (dimension.reflowScale !== undefined) {
    expect(await page.evaluate(() => window.innerWidth)).toBe(320);
    expect(await page.evaluate(() => matchMedia("(width < 40rem)").matches)).toBe(true);
  }
};
const primaryByFrame: Readonly<Record<string, string | null>> = {
  "case-undecided": "case.decide",
  "case-decided": "case.recordPayment",
  "case-paid": "case.close",
  "case-closed": null,
  "case-voided": null,
  "case-erased": null,
  "history-comparison": "case.close",
};
export const checkFrame = async (
  page: Page,
  dimension: Dimension,
  frame: string,
  _info: TestInfo,
) => {
  const violations = await geometryFailures(page);
  if (violations.length > 0) {
    const tableBounds = await page.locator(".data-table").evaluateAll((tables) =>
      tables.map((table) => {
        const content = table.querySelector("table")!;
        return {
          container: table.clientWidth,
          overflow: table.scrollWidth - table.clientWidth,
          mode: getComputedStyle(content).getPropertyValue("--table-layout").trim(),
          maxHeight: Math.max(
            0,
            ...Array.from(
              content.querySelectorAll("tbody tr:not(.table-group)"),
              (row) => row.getBoundingClientRect().height,
            ),
          ),
          tallRows: [...content.querySelectorAll("tbody tr:not(.table-group)")]
            .filter((row) => row.getBoundingClientRect().height > 72)
            .slice(0, 3)
            .map((row) =>
              Array.from(row.querySelectorAll("td"), (cell) => ({
                width: cell.getBoundingClientRect().width,
                content: cell.querySelector(".data-table-cell")?.getBoundingClientRect().height,
                chips: [...cell.querySelectorAll(".chip")].map((chip) => ({
                  width: chip.getBoundingClientRect().width,
                  text: chip.textContent.trim(),
                })),
              })),
            ),
          protrusions: [
            ...content.querySelectorAll(
              "tbody td, tbody .chip, tbody .data-table-cell, tbody .identifier-value",
            ),
          ]
            .flatMap((cell) => {
              const edge = table.getBoundingClientRect();
              const box = cell.getBoundingClientRect();
              const spill = Math.max(edge.left - box.left, box.right - edge.right);
              return box.width > 1 && box.height > 1 && spill > 1
                ? [{ width: box.width, spill, text: cell.textContent.trim().slice(0, 55) }]
                : [];
            })
            .slice(0, 4),
          columns: Array.from(
            content.querySelectorAll("thead th"),
            (cell) => cell.getBoundingClientRect().width,
          ),
        };
      }),
    );
    const controls = violations.includes("G8")
      ? await page.locator("main form, .toolbar, [role=dialog] form").evaluateAll((forms) =>
          forms.flatMap((form) =>
            [
              ...form.querySelectorAll(
                ".control-box, button, input:not([type=hidden],[type=radio],[type=checkbox],[type=file])",
              ),
            ].flatMap((control) => {
              const { x, y, width, height } = control.getBoundingClientRect();
              return width > 1 && height > 1
                ? [
                    {
                      x,
                      y,
                      width,
                      height,
                      kind: control.tagName,
                      name:
                        control.getAttribute("name") ??
                        control.getAttribute("aria-label") ??
                        control.className,
                    },
                  ]
                : [];
            }),
          ),
        )
      : [];
    const appearance = await page.evaluate(() => {
      const main = document.querySelector("main");
      const title = main?.querySelector("h1");
      const split: Array<{ token: string; element: string }> = [];
      for (const element of document.querySelectorAll(
        "main td, main th, .chip, button, [role=tab], nav a, main h1, main h2, main h3",
      )) {
        const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
        const range = document.createRange();
        for (let text = walker.nextNode(); text !== null; text = walker.nextNode()) {
          if (
            !(text instanceof Text) ||
            text.parentElement === null ||
            (innerWidth < 640 && text.parentElement.closest(".identifier-value") !== null)
          ) {
            continue;
          }
          if (text.parentElement.closest(".sr-only, thead[hidden]") !== null) {
            continue;
          }
          for (const word of text.data.matchAll(/\S+/gu)) {
            range.setStart(text, word.index);
            range.setEnd(text, word.index + word[0].length);
            if (
              new Set(
                [...range.getClientRects()]
                  .filter((rect) => rect.width > 0)
                  .map((rect) => Math.round(rect.top)),
              ).size > 1
            ) {
              split.push({ token: word[0].slice(0, 50), element: element.className.toString() });
            }
          }
        }
      }
      return {
        title: document.title,
        heading: title?.textContent.trim(),
        offset:
          main === null || title === null || title === undefined
            ? null
            : title.getBoundingClientRect().top - main.getBoundingClientRect().top,
        mainPadding: main === null ? null : getComputedStyle(main).paddingBlockStart,
        split: split.slice(0, 5),
      };
    });
    expect(violations, `${frame} ${JSON.stringify({ tableBounds, controls, appearance })}`).toEqual(
      [],
    );
  }
  await expectNoInlineStyles(page);
  await expectAccessible(page);
  await checkActionHierarchy(page, primaryByFrame[frame]);
  await noHorizontalOverflow(page, dimension.reflowScale === undefined ? "narrow" : "zoom");
};
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
  await mkdir(configured, {
    recursive: true,
    mode: 0o700,
  });
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
    fullPage: frame !== "paid-correction-review" && frame !== "record-payment-review",
    animations: "disabled",
  });
  await info.attach(`design-${frame}`, {
    path,
    contentType: "image/png",
  });
  return path;
};

export { headingOffset, headingOffsetFailures, feedbackFailures } from "./design-geometry";
