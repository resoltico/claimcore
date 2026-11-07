import { dimensions, dimensionCaseTitle, type Dimension } from "./design-matrix-plan";
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
  visitRoute,
  matrixEvidenceDirectory,
} from "./design-matrix-checks";
const engine = process.env["CLAIMCORE_WEB_E2E_ENGINE"] ?? "chromium";
if (!["chromium", "firefox", "webkit"].includes(engine)) {
  throw new Error(
    "Design matrix requires CLAIMCORE_WEB_E2E_ENGINE=chromium|firefox|webkit and matching --project.",
  );
}
const plannedDimensions = dimensions(engine);
let dataset: DesignDataset;
type Run = {
  page: Page;
  info: TestInfo;
  dimension: Dimension;
};
const recordFrame = async (run: Run, frame: string) => {
  try {
    await checkFrame(run.page, run.dimension, frame, run.info);
  } catch (error) {
    await run.page.screenshot({ path: run.info.outputPath(`failed-${frame}.png`), fullPage: true });
    throw error;
  }
  await captureFrame(run.page, run.info, run.dimension, frame);
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
  await expect(run.page.locator('.worklist-filters [role="combobox"][value="RECENT"]')).toHaveCount(
    1,
  );
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
    await run.page.getByRole("dialog").locator('button[type="submit"]').click();
    await expect(run.page.locator(".ui-dialog")).toBeVisible();
    await expect(
      run.page.getByRole("dialog").locator("button[data-confirm-operation]"),
    ).toBeEnabled();
    await expect(run.page.getByRole("dialog").getByRole("checkbox")).toHaveCount(0);
    expect(executions).toBe(0);
  } finally {
    run.page.off("request", observe);
  }
};
const historyComparison = async (page: Page) => {
  const first = page.locator(".case-timeline > .timeline-entry").first();
  await expect(first).toBeVisible();
  await first.locator(":scope > details > summary").click();
  await expect(first.locator("details .timeline-changes")).toBeVisible();
};
const performRoute = async (route: MatrixRoute, run: Run) => {
  await test.step(route.id, async () => {
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
  test(dimensionCaseTitle(dimension), { tag: ["@engine"] }, async ({ page }, info) => {
    test.setTimeout(240_000);
    expect(info.project.name).toBe(engine);
    page.setDefaultTimeout(15_000);
    await page.context().addCookies(dataset.cookies);
    await configureDimension(page, dimension);
    const run: Run = { page, info, dimension };
    for (const route of dataset.routes) {
      await performRoute(route, run);
    }
  });
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
  });
  test.afterAll(async () => {
    await dataset?.close();
  });
  plannedDimensions.forEach(registerDimension);
});
