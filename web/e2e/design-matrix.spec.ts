import { geometryFailures, narrowTableFailures } from "./design-geometry";
import {
  dimensions,
  limitDimensions,
  dimensionCaseTitle,
  type Dimension,
} from "./design-matrix-plan";
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
  headingOffset,
  headingOffsetFailures,
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
    await checkFrame(run.page, run.dimension, frame, run.info, dataset.caseMarker);
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
  const main = run.page.locator(".worklist-toolbar");
  if (run.dimension.width < 640 || run.dimension.reflowScale !== undefined) {
    await main.getByRole("button").last().click();
  }
  const grid = run.page.locator(".worklist-filter-controls");
  const trigger = grid.locator('button[aria-haspopup="dialog"]');
  await expect(trigger).toHaveCount(1);
  await expect(grid.locator('[role="combobox"][value="RECENT"]')).toHaveCount(1);
  await trigger.focus();
  await trigger.press("Enter");
  const panel = run.page.getByRole("dialog").last();
  await expect(panel.locator(".report-date-fields")).toBeVisible();
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
const casesLayoutBoundaries = async (page: Page) => {
  const table = page.locator(".worklist .data-table table");
  await expect(table.locator("tbody tr").first()).toBeVisible();
  const viewport = page.viewportSize()!;
  try {
    for (const width of [
      513, 514, 639, 640, 913, 914, 928, 929, 1023, 1024, 1105, 1106, 1120, 1121,
    ]) {
      await page.setViewportSize({ ...viewport, width });
      const failures = await geometryFailures(page, false, dataset.caseMarker);
      expect(
        failures.filter((id) => id === "G4" || id === "G5" || id === "G9"),
        `Seeded Cases at ${width}px`,
      ).toEqual([]);
      const observed = await table.evaluate((element) => ({
        container: element.parentElement!.clientWidth,
        rem: parseFloat(getComputedStyle(document.documentElement).fontSize),
        layout: getComputedStyle(element).getPropertyValue("--table-layout").trim(),
      }));
      const available = observed.container / observed.rem;
      const thresholds = [
        [64, "wide"],
        [50, "medium"],
      ] as const;
      const layout = thresholds.find(([minimum]) => available >= minimum)?.[1] ?? "narrow";
      expect(observed.layout, `Cases layout at ${width}px (${available}rem available)`).toBe(
        layout,
      );
    }
  } finally {
    await page.setViewportSize(viewport);
  }
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
  const parties = page.locator(".task-collection");
  await parties.getByRole("button").last().click();
  const party = parties.locator(".collection-item").last();
  await expect(party.getByRole("radiogroup").getByRole("radio").first()).toBeVisible();
  await party.getByRole("button").last().click();
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
    if (
      run.dimension.language === "ar" &&
      run.dimension.width === 320 &&
      (route.id === "cases-all" || route.id === "reminders")
    ) {
      const table = run.page.getByRole("table").first();
      await expect(table).toBeVisible();
      await expect(table.getByRole("columnheader").first()).toBeAttached();
      await expect(table.getByRole("cell").first()).toBeVisible();
      expect(await narrowTableFailures(run.page)).toEqual([]);
      const viewport = run.page.viewportSize()!;
      try {
        await run.page.setViewportSize({ ...viewport, width: 390 });
        expect(await narrowTableFailures(run.page)).toEqual([]);
      } finally {
        await run.page.setViewportSize(viewport);
      }
    }
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
    const offsets: number[] = [];
    const measured = new Set([
      "cases-all",
      "reminders",
      "people-list",
      "people-history",
      "settings",
    ]);
    for (const route of dataset.routes) {
      await performRoute(route, run);
      if (measured.has(route.id)) {
        offsets.push(await headingOffset(page));
      }
    }
    if (dimension.fixture === undefined) {
      expect(offsets).toHaveLength(measured.size);
      expect(headingOffsetFailures(offsets), JSON.stringify(offsets)).toEqual([]);
    }
  });
};
const registerCasesLayoutBoundaries = (language: "en" | "ar") => {
  test(`seeded Cases fit at the table and sidebar transitions in ${language} @engine`, async ({
    page,
  }) => {
    const dimension: Dimension = { language, width: 640, mode: "light", accent: "green" };
    await page.context().addCookies(dataset.cookies);
    await configureDimension(page, dimension);
    const cases = dataset.routes.find(({ id }) => id === "cases-all")!;
    await visitRoute(page, cases, dimension);
    await casesLayoutBoundaries(page);
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
  test(
    "case action drawers retain the case route and restore the opener",
    { tag: ["@engine"] },
    async ({ page }) => {
      const dimension: Dimension = { language: "en", width: 1440, mode: "light", accent: "green" };
      await page.context().addCookies(dataset.cookies);
      await configureDimension(page, dimension);
      const read = dataset.routes.find(({ id }) => id === "case-undecided")!;
      await visitRoute(page, read, dimension);
      const heading = page.locator("main #case-detail-title");
      const reference = (await heading.textContent())!.trim();
      const caseUrl = page.url();
      const opener = page.locator(".case-title-line .ui-button--primary").first();
      await opener.click();
      const drawer = page.getByRole("dialog");
      await expect(drawer).toBeVisible();
      await expect(page.locator("main h1")).toHaveCount(1);
      await expect(heading).toHaveText(reference);
      await expect(drawer.locator(".case-title-line .identifier-value")).toHaveText(reference);
      await expect(page).toHaveTitle(`Record payment decision · ${reference} · ClaimCore`);
      const actUrl = page.url();

      await page.keyboard.press("Escape");
      await expect(page).toHaveURL(caseUrl);
      await expect(opener).toBeFocused();

      await opener.click();
      await expect(drawer).toBeVisible();
      await page.goBack();
      await expect(page).toHaveURL(caseUrl);
      await expect(opener).toBeFocused();

      await page.goto(actUrl);
      await expect(drawer).toBeVisible();
      await page.keyboard.press("Escape");
      await expect(page).toHaveURL(caseUrl);
      await expect(heading).toBeFocused();
    },
  );
  test("payment entry selection uses the declared prompt and visible row action @engine", async ({
    page,
  }) => {
    const dimension: Dimension = { language: "en", width: 1440, mode: "light", accent: "green" };
    await page.context().addCookies(dataset.cookies);
    await configureDimension(page, dimension);
    const route = dataset.routes.find(({ id }) => id === "case-paid")!;
    await visitRoute(page, route, dimension);
    const caseUrl = page.url();
    await page.goto(`${caseUrl}/acts/case.correctPayment`);
    const dialog = page.getByRole("dialog");
    const prompt = dialog.getByText("Choose the payment entry to correct.");
    await expect(prompt).toBeFocused();
    await expect(dialog.locator(".case-title-line")).toHaveCount(1);
    await expect(dialog.locator("meter, .money-meter, .section-card")).toHaveCount(0);
    const action = dialog.locator("tbody").getByRole("button").first();
    await expect(action).toBeVisible();
    await action.click();
    await expect(dialog.locator(".lookup-input").getByRole("combobox")).toBeVisible();
    await expect(prompt).toHaveCount(0);
    await page.keyboard.press("Escape");
    await expect(page).toHaveURL(caseUrl);
    await expect(page.locator("main h1")).toBeFocused();
  });
  (["en", "ar"] as const).forEach(registerCasesLayoutBoundaries);
});

const registerLimitBoundaries = () => {
  test("limits fit at every list container transition @engine", async ({ page }) => {
    test.setTimeout(180_000);
    const dimension: Dimension = {
      language: "en",
      width: 1800,
      mode: "light",
      accent: "green",
      fixture: "limits",
    };
    await page.context().addCookies(dataset.cookies);
    await configureDimension(page, dimension);
    for (const route of dataset.routes.filter(({ id }) =>
      ["cases-all", "reminders", "people-list", "people-history"].includes(id),
    )) {
      await visitRoute(page, route, dimension);
      const isTimeline = route.id === "people-history";
      const list = page
        .locator(isTimeline ? "main .region-timeline" : "main .data-table table")
        .first();
      await expect(
        list.locator(isTimeline ? ":scope > li" : "tbody tr:not(.table-group)").first(),
      ).toBeVisible();
      const mode = () =>
        list.evaluate((element) =>
          getComputedStyle(element).getPropertyValue("--table-layout").trim(),
        );
      const resize = (width: number) => page.setViewportSize({ width, height: 900 });
      for (const [layout, lower, upper] of [
        ["narrow", 320, 320],
        ["medium", 320, 1023],
        ["wide", 1024, 1800],
      ] as const) {
        let left: number = lower;
        let right: number = upper;
        while (left < right) {
          const middle = (left + right) >>> 1;
          await resize(middle);
          if ((await mode()) === layout) {
            right = middle;
          } else {
            left = middle + 1;
          }
        }
        await resize(left);
        expect(await mode(), `${route.id} at ${left}px`).toBe(layout);
        const failures = (await geometryFailures(page, true)).filter(
          (rule) => rule === "G4" || rule === "G18",
        );
        const bounds = await list.boundingBox();
        if (layout === "wide" && !isTimeline) {
          const threshold = await list.evaluate((element) => ({
            width: element.parentElement!.clientWidth,
            rem: parseFloat(getComputedStyle(document.documentElement).fontSize),
          }));
          expect(threshold.width).toBeGreaterThanOrEqual(64 * threshold.rem);
          expect(threshold.width).toBeLessThanOrEqual(64 * threshold.rem + 1);
        }
        expect(
          failures,
          `${route.id} ${layout} viewport ${left}px content ${bounds?.width}px`,
        ).toEqual([]);
      }
      await resize(dimension.width);
    }
  });
};

test.describe("work surface with maximum accepted values", () => {
  test.beforeAll(async ({ browser, baseURL }) => {
    test.setTimeout(120_000);
    if (baseURL === undefined) {
      throw new Error("E2E_MATRIX_BASE_URL_REQUIRED");
    }
    dataset = await prepareDesignDataset(browser, baseURL, "limits");
  });
  test.afterAll(async () => {
    await dataset?.close();
  });
  limitDimensions(engine).forEach(registerDimension);
  registerLimitBoundaries();
});
