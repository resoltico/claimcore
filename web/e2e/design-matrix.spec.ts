import { chooseChoice, settledWorkspaceHeader } from "./workspace-navigation";
import { fillBusinessField } from "./typed-entry";
import { expect, test, type Page, type TestInfo, type Request } from "@playwright/test";
import {
  prepareDesignDataset,
  type DesignDataset,
  type MatrixRoute,
} from "./design-matrix-fixtures";
import {
  captureFrame,
  checkFrame,
  configureDimension,
  displayLocaleFor,
  dimensions,
  visitRoute,
  writeMatrixReport,
  expectedCaptures,
  type Dimension,
  matrixEvidenceDirectory,
} from "./design-matrix-checks";
const engine = process.env["CLAIMCORE_WEB_E2E_ENGINE"] ?? "chromium";
if (!["chromium", "firefox", "webkit"].includes(engine)) {
  throw new Error(
    "Design matrix requires CLAIMCORE_WEB_E2E_ENGINE=chromium|firefox|webkit and matching --project.",
  );
}
let dataset: DesignDataset;
type FrameCounts = { pageLoads: number; axeAndReflowChecks: number; screenshots: number };
type EngineCounts = FrameCounts & { dimensions: number };
const completed: FrameCounts[] = [];
const expectedRoutes = [
  "cases-mine",
  "cases-notified",
  "cases-summary",
  "cases-open-undecided",
  "cases-decided-unpaid",
  "cases-recent",
  "cases-closed",
  "cases-voided",
  "cases-all",
  "cases-filtered",
  "case-undecided",
  "case-decided",
  "case-paid",
  "case-closed",
  "case-voided",
  "case-erased",
  "editor-open",
  "editor-decide",
  "editor-correct",
  "editor-void",
  "editor-work",
  "recovery",
  "paid-correction-review",
  "history-comparison",
  "operation-result",
  "authority-history",
  "not-found",
  "authority-list",
  "authority-detail",
] as const;
type Run = {
  page: Page;
  info: TestInfo;
  dimension: Dimension;
  loads: string[];
  checked: string[];
  captures: string[];
};
const recordFrame = async (run: Run, frame: string) => {
  try {
    await checkFrame(run.page, run.dimension, frame);
  } catch (error) {
    await run.page.screenshot({ path: run.info.outputPath(`failed-${frame}.png`), fullPage: true });
    throw error;
  }
  run.checked.push(frame);
  const path = await captureFrame(run.page, run.info, run.dimension, frame);
  if (path !== null) {
    run.captures.push(path);
  }
};
const expandedFilters = async (run: Run) => {
  const compact = run.dimension.width < 640;
  await settledWorkspaceHeader(run.page);
  const trigger = run.page.locator('.worklist-toolbar > button[aria-haspopup="dialog"]');
  const viewport = await run.page.evaluate(() => ({
    width: innerWidth,
    compact: matchMedia("(width < 40rem)").matches,
  }));
  await expect(
    trigger,
    JSON.stringify({ requested: run.dimension.width, actual: viewport }),
  ).toHaveCount(compact ? 1 : 0);
  if (compact) {
    await trigger.click();
  }
  const summaries = run.page.locator(".worklist-filters details > summary");
  await expect(summaries).toHaveCount(3);
  for (const summary of await summaries.all()) {
    await summary.click();
  }
  await expect(run.page.locator(".worklist-filters input:checked")).toHaveCount(3);
  await expect(
    run.page
      .locator(".worklist-filters select")
      .filter({ has: run.page.locator('option[value="RECENT"]') }),
  ).toHaveValue("RECENT");
  await recordFrame(run, "cases-filter-panel");
  if (compact) {
    await run.page.keyboard.press("Escape");
  }
};
const paidCorrectionReview = async (run: Run) => {
  await chooseChoice(run.page, "act.groups.decision.kind", "UPDATE");
  await run.page.locator('input[data-selects-fields~="payableAmount"]').check();
  const amount = new Intl.NumberFormat(displayLocaleFor(run.dimension.language), {
    useGrouping: false,
    minimumFractionDigits: 2,
  }).format(300.26);
  await fillBusinessField(run.page, "payableAmount", amount);
  await chooseChoice(run.page, "act.groups.payment.kind", "REAFFIRM");
  let executions = 0;
  const observe = (request: Request) => {
    if (new URL(request.url()).pathname === "/api/operation.execute") {
      executions += 1;
    }
  };
  run.page.on("request", observe);
  try {
    await run.page.locator('.operation-editor form button[type="submit"]').click();
    await expect(run.page.locator(".review-modal")).toBeVisible();
    await expect(run.page.locator(".review-modal button.primary")).toBeDisabled();
    await expect(run.page.locator(".review-modal").getByRole("checkbox")).not.toBeChecked();
    expect(executions).toBe(0);
  } finally {
    run.page.off("request", observe);
  }
};
const historyComparison = async (page: Page) => {
  await page.locator(".case-history select").selectOption("FULL");
  const first = page.locator(".history-list > li > details").first();
  await expect(first).toBeVisible();
  await first.locator(":scope > summary").click();
  await expect(first.locator(".history-changes")).toBeVisible();
};
const performRoute = async (route: MatrixRoute, run: Run) => {
  await test.step(route.id, async () => {
    run.loads.push(route.id);
    await visitRoute(run.page, route, run.dimension);
    if (route.id === "paid-correction-review") {
      await paidCorrectionReview(run);
    }
    if (route.id === "history-comparison") {
      await historyComparison(run.page);
    }
    await recordFrame(run, route.id);
    if (route.filtered === true) {
      await expandedFilters(run);
    }
  });
};
const registerDimension = (dimension: Dimension) => {
  test(`${dimension.language} ${dimension.width}px ${dimension.theme}${dimension.zoom === undefined ? "" : " at 200% zoom"}`, async ({
    page,
  }, info) => {
    test.setTimeout(240_000);
    expect(info.project.name).toBe(engine);
    page.setDefaultTimeout(15_000);
    await page.context().addCookies(dataset.cookies);
    await configureDimension(page, dimension);
    const run: Run = { page, info, dimension, loads: [], checked: [], captures: [] };
    try {
      for (const route of dataset.routes) {
        await performRoute(route, run);
      }
      expect(run.loads).toEqual(expectedRoutes);
      expect(run.checked).toHaveLength(expectedRoutes.length + 1);
      expect(run.captures).toHaveLength(expectedCaptures(engine, dimension));
    } finally {
      completed.push({
        pageLoads: run.loads.length,
        axeAndReflowChecks: run.checked.length,
        screenshots: run.captures.length,
      });
      await writeMatrixReport(info, {
        engine,
        dimension,
        expected: {
          pageLoads: expectedRoutes.length,
          axeAndReflowChecks: expectedRoutes.length + 1,
          screenshots: expectedCaptures(engine, dimension),
        },
        actual: {
          pageLoads: run.loads.length,
          axeAndReflowChecks: run.checked.length,
          screenshots: run.captures.length,
        },
        loadedRoutes: run.loads,
        checkedFrames: run.checked,
        captures: run.captures,
      });
    }
  });
};
const reportEngine = async (info: TestInfo) => {
  const expected =
    engine === "chromium"
      ? { dimensions: 26, pageLoads: 754, axeAndReflowChecks: 780, screenshots: 91 }
      : { dimensions: 4, pageLoads: 116, axeAndReflowChecks: 120, screenshots: 0 };
  const actual = completed.reduce<EngineCounts>(
    (total, value) => ({
      dimensions: total.dimensions + 1,
      pageLoads: total.pageLoads + value.pageLoads,
      axeAndReflowChecks: total.axeAndReflowChecks + value.axeAndReflowChecks,
      screenshots: total.screenshots + value.screenshots,
    }),
    { dimensions: 0, pageLoads: 0, axeAndReflowChecks: 0, screenshots: 0 },
  );
  await writeMatrixReport(info, { engine, expected, actual }, "design-matrix-engine-counts");
  expect(actual).toEqual(expected);
};
test.describe("adopted work surface design matrix", () => {
  test.beforeAll(async ({ browser, baseURL }, info) => {
    test.setTimeout(120_000);
    expect(info.project.name).toBe(engine);
    process.stdout.write(`Design matrix durable evidence: ${await matrixEvidenceDirectory()}\n`);
    if (baseURL === undefined) {
      throw new Error("E2E_MATRIX_BASE_URL_REQUIRED");
    }
    dataset = await prepareDesignDataset(browser, baseURL);
    expect(dataset.routes.map((route) => route.id)).toEqual(expectedRoutes);
    expect(new Set(dataset.routes.map((route) => route.id)).size).toBe(expectedRoutes.length);
  });
  test.afterAll(async () => {
    try {
      await reportEngine(test.info());
    } finally {
      await dataset?.close();
    }
  });
  dimensions(engine).forEach(registerDimension);
});
