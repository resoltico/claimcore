import { expect, test } from "./fixtures";
import { configureDimension } from "./design-matrix-checks";
import {
  geometryFailures,
  tableLayoutState,
  isWideTable,
  stableTableLayout,
} from "./design-geometry";
import { prepareDesignDataset, type DesignDataset } from "./design-matrix-fixtures";
import { settledWorkspaceHeader } from "./workspace-navigation";

const standardCasesPaths = (dataset: DesignDataset) => {
  const scope = dataset.routes.find(({ id }) => id === "cases-received")!;
  const search = new URL(scope.path, "https://workspace.example").searchParams.get("q")!;
  return ["/cases", "/cases?view=ALL", "/cases?view=MINE"].map((path) => {
    const url = new URL(path, "https://workspace.example");
    url.searchParams.set("q", search);
    return url.pathname + url.search;
  });
};
const registerFixture = (fixture: "standard" | "limits", standard: () => DesignDataset) => {
  test.describe(`classic scrollbar ${fixture} fixture`, () => {
    let dataset: DesignDataset;
    test.beforeAll(async ({ browser, baseURL }) => {
      test.setTimeout(120_000);
      dataset =
        fixture === "standard"
          ? standard()
          : await prepareDesignDataset(browser, baseURL!, fixture);
    });
    test.afterAll(async () => {
      if (fixture === "limits") {
        await dataset?.close();
      }
    });

    [1440, 1366, 1280].forEach((width) => {
      test(`lists and drawer at ${width}px`, async ({ page }) => {
        test.setTimeout(120_000);
        await page.context().addCookies(dataset.cookies);
        await configureDimension(page, { width, language: "en", mode: "light", accent: "blue" });
        const list = page.locator("main .data-table");
        const casesAll = dataset.routes.find(({ id }) => id === "cases-all")!;
        const cases =
          fixture === "standard"
            ? standardCasesPaths(dataset)
            : ["/cases", casesAll.path, "/cases?view=MINE"];
        for (const path of [...cases, "/reminders", "/reminders?view=MINE", "/people"]) {
          await page.goto(path);
          await settledWorkspaceHeader(page);
          const rows = list.locator("tbody tr");
          await expect(rows.first()).toBeVisible();
          if (fixture === "standard" && path.startsWith("/cases")) {
            await expect(rows.first()).toContainText(dataset.caseMarker);
            await expect(rows.filter({ hasNotText: dataset.caseMarker })).toHaveCount(0);
          }
          const atTall = await tableLayoutState(page);
          if (width >= 1366) {
            expect(isWideTable(atTall), `G29 ${path}`).toBe(true);
          }
          const failures = await geometryFailures(page, fixture === "limits");
          expect(
            failures.filter((id) => ["G5", "G5b", "G5c", "G30"].includes(id)),
            path,
          ).toEqual([]);
          const viewport = page.viewportSize()!;
          await page.setViewportSize({ width, height: 440 });
          expect(
            stableTableLayout(atTall, await tableLayoutState(page)),
            `G29 ${path} document overflow`,
          ).toBe(true);
          await page.setViewportSize(viewport);

          if (path.startsWith("/cases")) {
            await page.locator(".open-case-link").click();
            await expect(page.getByRole("dialog")).toBeVisible();
            expect(
              stableTableLayout(atTall, await tableLayoutState(page)),
              `G29 ${path} drawer`,
            ).toBe(true);
            await page.keyboard.press("Escape");
          }
        }
      });
    });
  });
};
test.describe("classic scrollbar matrix", () => {
  let standard: DesignDataset;
  test.beforeAll(async ({ browser, baseURL }) => {
    test.setTimeout(120_000);
    standard = await prepareDesignDataset(browser, baseURL!);
  });
  test.afterAll(async () => {
    await standard?.close();
  });
  (["standard", "limits"] as const).forEach((fixture) => {
    registerFixture(fixture, () => standard);
  });
});
