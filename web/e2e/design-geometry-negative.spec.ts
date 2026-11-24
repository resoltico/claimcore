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
import { clippedControlEdges } from "./layout-support";

test("drawer containment accepts fractional fits and rejects clipping @engine", async ({
  page,
}) => {
  await page.setViewportSize({ width: 320, height: 900 });
  await page.setContent(`<!doctype html>
    <main dir="rtl"><div class="scrollport"><textarea></textarea></div></main>
    <style>
      .scrollport { width: 292.40625px; height: 128px; overflow: hidden; border: 1px solid; }
      textarea { display: block; box-sizing: border-box; width: 100%; height: 100%; }
    </style>
  `);
  const control = page.locator("textarea");
  expect(await clippedControlEdges(control)).toEqual([]);
  await page.addStyleTag({ content: "textarea { transform: translateX(0.5px); }" });
  expect(await clippedControlEdges(control)).toEqual(["scrollport: right"]);
  await page.addStyleTag({ content: "textarea { transform: translateY(1px); }" });
  expect(await clippedControlEdges(control)).toEqual(["scrollport: bottom"]);
});

test("drawer containment rejects clipping at the viewport edge @engine", async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 900 });
  await page.setContent(`<!doctype html>
    <style>textarea { position: fixed; bottom: 0; left: 16px; }</style>
    <textarea></textarea>
  `);
  const control = page.locator("textarea");
  expect(await clippedControlEdges(control)).toEqual([]);
  await page.addStyleTag({ content: "textarea { bottom: -1px; }" });
  expect(await clippedControlEdges(control)).toEqual(["viewport: bottom"]);
});

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
          <table><tbody><tr><td>Other Case</td><td class="data-table-actions">
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
    button, input { font: inherit }
    .chip-list { display: flex; align-items: center; list-style: none }
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
  ["G4", ".toolbar { width: 25px; overflow: hidden } .toolbar input { width: 200px }"],
  ["G5", ".data-table tr { height: 120px }"],
  ["G6", ".data-table button.extra { display: inline-block }"],
  ["G8", "input { block-size: 24px }"],
  [
    "G9",
    ".data-table table { --table-layout: medium; width: 100% } td:first-child { word-break: break-all; max-width: 7ch; display: inline-block }",
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

const limitsWitnesses: ReadonlyArray<readonly [GeometryId, string, string]> = [
  [
    "G17",
    '<div class="token"><bdi class="identifier-value">ABCDEFGH</bdi></div>',
    ".token { width: 3ch; overflow-wrap: anywhere }",
  ],
  [
    "G18",
    "",
    ".data-table { width: 30rem } .data-table-actions button { transform: translateX(30rem) }",
  ],
  [
    "G19",
    '<div class="drawer"><div class="control-box"><button>Options</button></div></div>',
    ".control-box { width: 20rem } .control-box button { border: 1px solid }",
  ],
  [
    "G20",
    '<dl><div class="pair"><dt>Label</dt><dd>Value</dd></div></dl>',
    ".pair { display: flex; gap: 12px } .pair dd { transform: translateY(8px) }",
  ],
  [
    "G21",
    '<div class="drawer"><div class="case-title-line"><bdi class="identifier-value">CASE-1</bdi></div><bdi class="identifier-value">CASE-1</bdi></div>',
    "",
  ],
  ["G22", "<ul><li>Marker</li></ul>", ""],
];
for (const [id, markup, defect] of limitsWitnesses) {
  test(`${id} rejects its workspace regression`, async ({ page }) => {
    await authorFixture(page);
    if (markup !== "") {
      await page.locator("main").evaluate((main, content) => {
        main.insertAdjacentHTML("beforeend", content);
      }, markup);
    }
    if (defect !== "") {
      await page.addStyleTag({ content: defect });
    }
    expect(await geometryFailures(page)).toContain(id);
  });
}

test("lowering the wide transition by 2rem rejects overflowing controls at its boundary", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.setContent(`
    <title>Cases · ClaimCore</title>
    <main><h1>Cases</h1>
      <div class="data-table"><div class="sample-row">Case <button>Complete</button></div></div>
    </main>
    <style>
      .data-table { inline-size: 62.5rem; container: boundary / inline-size; }
      .sample-row { display: flex; justify-content: space-between; inline-size: 100%; }
      @container boundary (width >= 64rem) { .sample-row { inline-size: 64rem; } }
    </style>
  `);
  const fit = async () =>
    (await geometryFailures(page, true)).filter((id) => id === "G4" || id === "G18");
  expect(await fit()).toEqual([]);
  await page.addStyleTag({
    content:
      "@container boundary (width >= 62rem) { .data-table .sample-row { inline-size: 64rem; } }",
  });
  expect(await fit()).toContain("G4");
});

test("G4 allows caret scrolling inside a bounded search field", async ({ page }) => {
  await authorFixture(page);
  await page.locator('input[type="search"]').fill("W".repeat(80));
  await page.addStyleTag({ content: ".toolbar input { inline-size: 18ch; overflow: clip }" });
  const scrolls = await page
    .locator('input[type="search"]')
    .evaluate((field) => field.scrollWidth > field.clientWidth);
  expect(scrolls).toBe(true);
  expect(await geometryFailures(page)).not.toContain("G4");
});

const anatomyWitnesses = [
  {
    id: "G5b",
    body: `<div class="worklist"><div class="data-table"><table><tbody><tr><td>
      <div class="chip-list"><span class="chip">Undecided</span><span class="chip">No next action</span><span class="chip">Unassigned</span></div>
      </td></tr></tbody></table></div></div>`,
    css: ".worklist table { --table-layout: wide } .chip-list { display: flex; flex-wrap: wrap; width: 18rem } .chip { width: 5.5rem }",
    defect: ".chip-list { width: 14rem }",
  },
  {
    id: "G10",
    body: `<div role="dialog"><div class="radio-option"><label>Choice</label></div><button>Review</button></div>`,
    css: "[role=dialog] { --accent-solid: #065ebb } button { background: var(--accent-solid) }",
    defect: ".radio-option > label { background: var(--accent-solid) }",
  },
  {
    id: "G19b",
    body: `<div class="control-box"><button>Open</button></div>`,
    css: ".control-box { border: 1px solid; height: 36px; display: flex } button { background: transparent; border: 0 }",
    defect: "button { margin-block: -1px }",
  },
  {
    id: "G19c",
    body: `<div class="task-field-grid"><div><div class="field-label-row">Payee</div><input class="control-box"><p class="field-hint">Choose a party</p></div>
      <div><div class="field-label-row">Reference</div><input class="control-box"><p class="field-hint">Enter a reference</p></div></div>`,
    css: ".task-field-grid { display: flex; gap: 12px }",
    defect: ".task-field-grid > :first-child .control-box { margin-block-start: 2px }",
  },
  {
    id: "G20b",
    body: `<div class="subject-header-facts"><dl><div><dt>Claimed amount</dt><dd>100 EUR</dd></div><div><dt>State</dt><dd>Open</dd></div></dl></div>`,
    css: "dl { display: grid; grid-template-columns: 10rem 10rem } dl > div { grid-column: 1 / -1; display: grid; grid-template-columns: subgrid } dd { margin: 0 }",
    defect: "dl > div { grid-template-columns: fit-content(45%) 1fr; width: 7rem }",
  },
  {
    id: "G21",
    body: `<section class="case-header-card"><section class="stages">Case stages</section></section>`,
    css: ".case-header-card { width: 500px; border: 1px solid; padding: 16px } .stages { height: 80px }",
    defect: ".stages { border: 1px solid }",
  },
  {
    id: "G23",
    body: `<div class="control-box"><input aria-label="Filter"></div>`,
    css: ".control-box:focus-within { outline: 2px solid } input { outline: 0 }",
    defect: "input:focus { outline: 2px solid }",
    focus: "input",
  },
  {
    id: "G24",
    body: `<div class="data-table"><table><tbody><tr><td><div class="data-table-cell">First</div></td><td><div class="data-table-cell">Second</div></td></tr></tbody></table></div>`,
    css: "table { border-collapse: collapse } td { padding-inline: 6px }",
    defect: "td { padding-inline: 4px }",
  },
  {
    id: "G25",
    body: `<div class="data-table"><table><tbody><tr><td><div class="data-table-cell">First<br>Two lines</div></td><td><div class="data-table-cell">Second</div></td></tr></tbody></table></div>`,
    css: "td { vertical-align: baseline; padding-inline: 6px }",
    defect: "td { vertical-align: middle }",
  },
  {
    id: "G26",
    body: `<section class="section-card"><div class="entries-view"><dl><div>Paid 10</div></dl></div><div class="entries-view"><dl><div>Decision</div></dl></div></section>`,
    css: ".section-card { display: grid; gap: 12px } dl { margin: 0 }",
    defect: ".section-card { gap: 0 }",
  },
  {
    id: "G27",
    body: `<section class="section-card"><header>Parties</header><div class="row">Claimant</div></section>`,
    css: "header { border-bottom: 1px solid } .row { margin-top: 18px }",
    defect: ".row { border-top: 1px solid }",
  },
  {
    id: "G28",
    body: `<div class="case-workspace"><div class="case-work-panels"><div class="case-area-next">Next</div><div class="case-area-work">Work</div></div>
      <div class="case-area-claim">Claim</div><div class="case-area-related">Related</div></div>`,
    css: ".case-workspace { display: grid; grid-template-columns: 2fr 1fr; grid-template-areas: 'claim lead' 'claim related'; grid-template-rows: auto 1fr; gap: 16px } .case-work-panels { grid-area: lead; display: grid; gap: 16px } .case-area-claim { grid-area: claim; height: 240px } .case-area-related { grid-area: related; align-self: start }",
    defect: ".case-work-panels { gap: 180px }",
  },
  {
    id: "G30",
    body: `<div class="worklist-toolbar"><div class="toolbar-actions"><button>More filters</button><button>Export</button></div></div>`,
    css: ".toolbar-actions { display: flex; gap: 8px }",
    defect: ".toolbar-actions { display: grid; justify-items: start }",
  },
] as const;

for (const witness of anatomyWitnesses) {
  test(`${witness.id} rejects its anatomy regression`, async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    await page.setContent(
      `<!doctype html><style>${witness.css}</style><main>${witness.body}</main>`,
    );
    if ("focus" in witness) {
      await page.locator(witness.focus).focus();
    }
    expect(await geometryFailures(page)).not.toContain(witness.id);
    await page.addStyleTag({ content: witness.defect });
    expect(await geometryFailures(page)).toContain(witness.id);
  });
}
