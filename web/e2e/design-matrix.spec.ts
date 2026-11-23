import { dimensions, dimensionCaseTitle, type Dimension } from "./design-matrix-plan";
import { chooseChoice, settledWorkspaceHeader } from "./workspace-navigation";
import { fillBusinessField } from "./typed-entry";
import { expect, test, type Page, type TestInfo, type Request } from "./fixtures";
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
    try {
      await run.page.screenshot({
        path: run.info.outputPath(`failed-${frame}.png`),
        fullPage: true,
      });
    } catch {
      // A closed/timed-out page must not replace the original failed check with a capture error.
    }
    throw error;
  }
  await captureFrame(run.page, run.info, run.dimension, frame);
};
const expandedFilters = async (run: Run) => {
  await settledWorkspaceHeader(run.page);
  const grid = run.page.locator(".worklist-toolbar .worklist-filter-controls");
  const trigger = grid.locator('button[aria-haspopup="dialog"]');
  await expect(trigger).toHaveCount(1);
  await expect(grid.locator('[role="combobox"][value="RECENT"]')).toHaveCount(1);
  await trigger.focus();
  await trigger.press("Enter");
  const panel = run.page.getByRole("dialog");
  await expect(panel.locator(".report-date-fields")).toBeVisible();
  const groups = panel.locator(".worklist-filters details > summary");
  await expect(groups).toHaveCount(3);
  for (const group of await groups.all()) {
    await group.click();
  }
  await expect(panel.locator(".worklist-filters input:checked")).toHaveCount(3);
  await recordFrame(run, "cases-filter-panel");
  await run.page.keyboard.press("Escape");
  await expect(trigger).toBeFocused();
};
const paidCorrectionReview = async (run: Run) => {
  await chooseChoice(run.page, "group:decision", "UPDATE");
  await run.page.locator('input[data-selects-fields~="payableAmount"]').check();
  const amount = new Intl.NumberFormat(displayLocaleFor(run.dimension.language), {
    useGrouping: false,
    minimumFractionDigits: 2,
  }).format(300.26);
  await fillBusinessField(run.page, "payableAmount", amount);
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
const recordPaymentReview = async (run: Run) => {
  await fillBusinessField(run.page, "paymentDate", dataset.businessDate);
  await fillBusinessField(
    run.page,
    "paidAmount",
    new Intl.NumberFormat(displayLocaleFor(run.dimension.language), {
      useGrouping: false,
      minimumFractionDigits: 2,
    }).format(100),
  );
  await fillBusinessField(run.page, "paidCurrency", "EUR");
  await run.page.getByRole("dialog").locator('button[type="submit"]').click();
  await expect(
    run.page.getByRole("dialog").locator("button[data-confirm-operation]"),
  ).toBeEnabled();
};
const historyComparison = async (page: Page) => {
  await expect(page.locator(".case-timeline-panel .region-timeline > li").first()).toBeVisible();
};
const editorLayout = async (page: Page) => {
  const section = page.locator(".task-field-section").filter({
    has: page.locator('[data-field-name="incidentDate"]'),
  });
  await expect(section.getByRole("heading")).toBeVisible();
  const date = await section.locator('[data-field-name="incidentDate"]').boundingBox();
  const received = await section.locator('[data-field-name="receivedDate"]').boundingBox();
  const narrative = await section.locator('[data-field-name="incidentDescription"]').boundingBox();
  expect(date).not.toBeNull();
  expect(received).not.toBeNull();
  expect(narrative).not.toBeNull();
  if ((page.viewportSize()?.width ?? 0) >= 640) {
    const drawer = await page.locator(".drawer").boundingBox();
    expect(drawer?.width).toBeGreaterThanOrEqual(640);
    expect(Math.abs(date!.y - received!.y)).toBeLessThan(2);
    expect(Math.abs(date!.x - received!.x)).toBeGreaterThan(date!.width);
    expect(narrative!.width).toBeGreaterThan(date!.width * 1.5);
  } else {
    expect(received!.y).toBeGreaterThan(date!.y);
  }
};
const performRoute = async (route: MatrixRoute, run: Run) => {
  await test.step(route.id, async () => {
    await visitRoute(run.page, route, run.dimension);
    if (route.id === "editor-open") {
      await editorLayout(run.page);
    }
    if (route.id === "paid-correction-review") {
      await paidCorrectionReview(run);
    }
    if (route.id === "record-payment-review") {
      await recordPaymentReview(run);
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
