import { expectNoInlineStyles } from "./inline-styles";
import { geometryFailures, type GeometryId } from "./design-geometry";
import { expect, test } from "@playwright/test";

const authorFixture = async (page: Parameters<typeof geometryFailures>[0]) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.setContent(`
    <aside><footer class="shell-footer">Account</footer></aside>
    <main class="workspace-main">
      <h1>Cases</h1>
      <div class="case-title-line">
        <ul class="chip-list"><li><span class="chip">Open</span></li><li><span class="chip">Due today</span></li></ul>
      </div>
      <div class="worklist">
        <div class="data-table">
          <table><tbody><tr><td>CASE-1</td><td class="data-table-actions">
            <button>Complete</button><button aria-haspopup="menu">More</button>
            <button class="extra" hidden>Cancel</button>
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
];
for (const [id, defect] of witnesses) {
  test(`${id} rejects a geometry regression`, async ({ page }) => {
    await authorFixture(page);
    await page.addStyleTag({ content: defect });
    expect(await geometryFailures(page)).toEqual([id]);
  });
}
test("G7 rejects a returned legal bar or record disclosure", async ({ page }) => {
  await authorFixture(page);
  await page.evaluate(() => {
    const bar = document.createElement("aside");
    bar.className = "legal-notice";
    document.body.prepend(bar);
    const details = document.createElement("details");
    document.querySelector(".data-table td")?.append(details);
  });
  expect(await geometryFailures(page)).toEqual(["G7"]);
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
