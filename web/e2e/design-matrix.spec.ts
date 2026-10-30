import { expect, test, type Page, type TestInfo } from "@playwright/test";
import {
  prepareDesignDataset,
  type DesignDataset,
  type MatrixRoute,
} from "./design-matrix-fixtures";
import {
  captureFrame,
  checkFrame,
  configureDimension,
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
    await checkFrame(run.page, run.dimension);
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
  if (compact) {
    await run.page.locator(".worklist-toolbar > button").first().click();
  }
  const summaries = await run.page.locator(".worklist-filters > details > summary").all();
  expect(summaries).toHaveLength(3);
  for (const summary of summaries) {
    await summary.click();
  }
  await expect(run.page.locator(".worklist-filters input:checked")).toHaveCount(3);
  await expect(run.page.locator(".worklist-filters select")).toHaveValue("RECENT");
  await recordFrame(run, "cases-filter-panel");
  if (compact) {
    await run.page.keyboard.press("Escape");
  }
};
const performRoute = async (route: MatrixRoute, run: Run) => {
  await test.step(route.id, async () => {
    run.loads.push(route.id);
    await visitRoute(run.page, route, run.dimension);
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
    await page.context().addCookies(dataset.cookies);
    await configureDimension(page, dimension);
    const run: Run = { page, info, dimension, loads: [], checked: [], captures: [] };
    try {
      for (const route of dataset.routes) {
        await performRoute(route, run);
      }
      expect(run.loads).toHaveLength(25);
      expect(run.checked).toHaveLength(26);
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
          pageLoads: 25,
          axeAndReflowChecks: 26,
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
      ? { dimensions: 26, pageLoads: 650, axeAndReflowChecks: 676, screenshots: 56 }
      : { dimensions: 4, pageLoads: 100, axeAndReflowChecks: 104, screenshots: 0 };
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
    expect(dataset.routes).toHaveLength(25);
    expect(new Set(dataset.routes.map((route) => route.id)).size).toBe(25);
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
