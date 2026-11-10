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
import "./localization-support";
import { isResponseFor } from "../src/protocol/client";
import type { MatrixRoute } from "./design-matrix-fixtures";
import { presentationDisplayOptions, presentationDefaults } from "@contracts/client";
export const displayLocaleFor = (language: Dimension["language"]) =>
  presentationDisplayOptions.find((option) => option.language === language)?.value ??
  presentationDefaults.displayLocale;
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
    await expect(page.locator(".entries-view [data-entry='claimantName']").first()).toBeVisible();
  }
  if (route.id === "case-erased") {
    await expect(page.locator('.region-notice[data-tone="FINAL"]')).toBeVisible();
    await expect(page.locator(".entries-view [data-entry='claimantName']")).toHaveCount(0);
  }
  if (route.id.startsWith("cases-")) {
    await expect(page.locator(".summary-band")).toBeVisible();
    await expect(page.locator(".summary-band[role=status], .summary-band[role=alert]")).toHaveCount(
      0,
    );
    await expect(page.locator('.view-tabs [role="tab"][aria-selected="true"]')).toHaveCount(1);
  }
  if (route.id.startsWith("case-")) {
    await expect(page.locator(".case-timeline-panel .region-timeline > li").first()).toBeVisible();
    await expect(
      page.locator('.code-value[data-vocabulary="assignment"][data-code="MINE"]'),
    ).toHaveCount(route.id === "case-undecided" ? 1 : 0);
  }
  if (route.id === "operation-result") {
    await expect(page.locator(".entries-view [data-entry='claimantName']")).toBeVisible();
  }
  if (route.id === "authority-list") {
    await expect(page.locator('a[id^="actor-grants-"]').first()).toBeVisible();
  }
  if (route.id === "authority-detail") {
    await expect(page.locator('[role="dialog"] .table-view tbody > tr')).toHaveCount(1);
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
  _dimension: Dimension,
) => {
  if (payload.outcome.kind !== "SUCCEEDED") {
    return;
  }
  const rows = page.locator(".table-view tbody > tr:not(.table-group)");
  await expect(rows).toHaveCount(payload.outcome.document.context.cases.length);
  const table = payload.outcome.document.screen.regions.find(
    (region) => region.role === "RESULTS",
  )?.content;
  if (table?.kind !== "TABLE") {
    throw new Error("The case page must supply its table view.");
  }
  for (const [index, item] of payload.outcome.document.context.cases.entries()) {
    const row = rows.nth(index);
    await expect(row.locator(`a[href="/cases/id/${item.caseId}"]`)).toBeVisible();
    await expect(row.locator("td[data-column]")).toHaveCount(table.columns.length);
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
  await expect(
    page.locator(`.table-view tbody > tr a[href="/cases/id/${assigned!.key}"]`),
  ).toBeVisible();
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
  if (route.endpoint === undefined || !isResponseFor(route.endpoint, "HUMAN", payload)) {
    throw new Error("E2E_MATRIX_READ_REPLY_INVALID");
  }
  expect(payload.outcome.kind).toBe(route.endpoint === "operation.get" ? "ACCEPTED" : "SUCCEEDED");
  if (route.endpoint === "case.list" && isResponseFor("case.list", "HUMAN", payload)) {
    await assertListPresentation(page, payload, route, dimension);
  }
  if (route.endpoint === "case.get" && isResponseFor("case.get", "HUMAN", payload)) {
    await assertCasePresentation(page, payload, route);
  }
  if (
    route.endpoint === "authority.grants" &&
    isResponseFor("authority.grants", "HUMAN", payload) &&
    payload.outcome.kind === "SUCCEEDED"
  ) {
    const { principal } = payload.outcome.document.context;
    const panel = page.getByRole("dialog");
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
    await page.locator("#case-work-summary").click();
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
const primaryByFrame: Readonly<Record<string, string | null>> = {
  "case-undecided": "DECIDE",
  "case-decided": "RECORD_PAYMENT",
  "case-paid": "CLOSE",
  "case-closed": null,
  "case-voided": null,
  "case-erased": null,
  "history-comparison": "CLOSE",
};
export const checkFrame = async (
  page: Page,
  dimension: Dimension,
  frame: string,
  _info: TestInfo,
) => {
  await expectAccessible(page);
  await checkActionHierarchy(page, primaryByFrame[frame]);
  await noHorizontalOverflow(page, dimension.zoom === undefined ? "narrow" : "zoom");
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
  await info.attach(`design-${frame}`, { path, contentType: "image/png" });
  return path;
};
