import { expectNoInlineStyles } from "./inline-styles";
import {
  feedbackFailures,
  headingOffset,
  headingOffsetFailures,
  geometryFailures,
  narrowTableFailures,
  type GeometryId,
} from "./design-geometry";
import { expect, test } from "@playwright/test";

const authorFixture = async (page: Parameters<typeof geometryFailures>[0]) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.setContent(`
    <title>Cases · ClaimCore</title>
    <aside><footer class="shell-footer">Account</footer></aside>
    <main class="workspace-main">
      <h1>Cases</h1>
      <div class="toolbar"><input type="search" aria-label="Filter"></div>
      <div class="case-title-line">
        <ul class="chip-list"><li><span class="chip">Open</span></li><li><span class="chip">Due today</span></li></ul>
      </div>
      <div class="worklist">
        <div class="data-table">
          <table><tbody><tr><td>Case</td><td class="data-table-actions">
            <button>Complete</button><button aria-haspopup="menu">More</button>
            <button class="extra">Cancel</button>
          </td></tr></tbody></table>
        </div>
      </div>
    </main>
  `);
  await page.addStyleTag({
    content: `
    h1 { margin: 0 }
    .chip-list { display: flex; align-items: center }
    .data-table { width: 100%; overflow: auto }
    .data-table table { --table-layout: wide }
    :where(.toolbar input) { height: 40px; box-sizing: border-box }
    .extra { display: none }
  `,
  });
  expect(await geometryFailures(page)).toEqual([]);
};

const witnesses: ReadonlyArray<readonly [GeometryId, string]> = [
  ["G1", ".workspace-main { padding-top: 90px }"],
  ["G2", ".shell-footer { transform: translateY(100vh) }"],
  ["G3", ".chip-list li + li .chip { display: inline-block; transform: translateY(8px) }"],
  ["G4", ".data-table table { min-width: 2000px }"],
  ["G5", ".data-table tr { height: 120px }"],
  ["G6", ".data-table button.extra { display: inline-block }"],
  ["G8", "input { block-size: 24px }"],
  [
    "G9",
    ".data-table table { --table-layout: medium } td:first-child { overflow-wrap: anywhere; inline-size: 1ch }",
  ],
  ["G12", "h1 { font-size: 11px }"],
];
for (const [id, defect] of witnesses) {
  test(`${id} rejects a geometry regression`, async ({ page }) => {
    await authorFixture(page);
    await page.addStyleTag({ content: defect });
    expect(await geometryFailures(page)).toEqual([id]);
  });
}
test("G10 rejects two primary actions", async ({ page }) => {
  await authorFixture(page);
  await page.locator("main").evaluate((main) => {
    for (const label of ["One", "Two"]) {
      const button = document.createElement("button");
      button.className = "ui-button--primary";
      button.textContent = label;
      main.append(button);
    }
  });
  expect(await geometryFailures(page)).toContain("G10");
});

test("G11 rejects mismatched page headings", async ({ page }) => {
  await authorFixture(page);
  const original = await headingOffset(page);
  expect(headingOffsetFailures([original, original])).toEqual([]);
  await page.addStyleTag({ content: "main h1 { transform: translateY(4px) }" });
  expect(headingOffsetFailures([original, await headingOffset(page)])).toEqual(["G11"]);
});

test("G13 rejects a disclosure outside a dialog", async ({ page }) => {
  await authorFixture(page);
  await page.locator("main").evaluate((main) => {
    main.append(document.createElement("details"));
  });
  expect(await geometryFailures(page)).toContain("G13");
});

test("G14 rejects a title that omits the heading", async ({ page }) => {
  await authorFixture(page);
  await page.evaluate(() => {
    document.title = "ClaimCore";
  });
  expect(await geometryFailures(page)).toContain("G14");
});

test("G15 rejects a list below the phone viewport target", async ({ page }) => {
  await authorFixture(page);
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await geometryFailures(page)).not.toContain("G15");
  await page.addStyleTag({ content: ".worklist { margin-top: 70vh }" });
  expect(await geometryFailures(page)).toContain("G15");
});

test("G16 rejects removal of the focused outcome banner", async ({ page }) => {
  await page.setContent(
    '<main><div class="page"><div class="status-banner" tabindex="-1">Reminder completed</div></div></main>',
  );
  await page.locator(".status-banner").focus();
  expect(await feedbackFailures(page, "Reminder completed")).toEqual([]);
  await page.locator(".status-banner").evaluate((banner) => {
    banner.remove();
  });
  expect(await feedbackFailures(page, "Reminder completed")).toEqual(["G16"]);
});

test("rejects both forms of inline styling", async ({ page }) => {
  await page.setContent("<main>Workspace</main>");
  await expectNoInlineStyles(page);

  await page.locator("main").evaluate((element) => {
    element.setAttribute("style", "margin:0");
  });
  await expect(expectNoInlineStyles(page)).rejects.toThrow("external stylesheets");
  await page.locator("main").evaluate((element) => {
    element.removeAttribute("style");
  });

  await page.addStyleTag({ content: "main { margin: 0 }" });
  await expect(expectNoInlineStyles(page)).rejects.toThrow("external stylesheets");
});

test("narrow table geometry rejects crushed words and clipped values", async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 900 });
  await page.setContent(`
    <main class="workspace-main">
      <div class="data-table">
        <table><tbody><tr><td data-prominence="PRIMARY">
          <div class="data-table-cell"><bdi class="plain-text-value">Contact claimant</bdi></div>
        </td></tr></tbody></table>
      </div>
    </main>
  `);
  expect(await narrowTableFailures(page)).toEqual([]);
  await page.addStyleTag({
    content:
      ".data-table-cell { width: 3ch; height: 1em; overflow: hidden; overflow-wrap: anywhere }",
  });
  expect(await narrowTableFailures(page)).toEqual(["clipped-primary-cell", "split-ordinary-word"]);
});
