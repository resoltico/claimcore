import { expectNoInlineStyles } from "./inline-styles";
import {
  feedbackFailures,
  focusFailures,
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

test("drawer containment detects focus paint outside an otherwise fitting scrollport @engine", async ({
  page,
}) => {
  await page.setContent(`<!doctype html>
    <style>
      .scrollport { width: 280px; overflow: auto; padding: 8px; }
      input { display: block; box-sizing: border-box; width: 100%; }
      input:focus { outline: 2px solid; outline-offset: 2px; }
    </style>
    <div class="scrollport"><input aria-label="Reference"></div>`);
  const input = page.getByRole("textbox");
  await input.focus();
  expect(await clippedControlEdges(input, true)).toEqual([]);
  await page.addStyleTag({ content: ".scrollport { padding-inline: 0; }" });
  expect(await clippedControlEdges(input)).toEqual([]);
  expect(await clippedControlEdges(input, true)).toEqual(["scrollport: left", "scrollport: right"]);
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
    td { padding-inline: 6px }
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
test("G5 measures only the standard fixture rows when a case scope is supplied", async ({
  page,
}) => {
  await authorFixture(page);
  const result = page.locator(".data-table td").first();
  await result.evaluate((cell) => {
    const link = document.createElement("a");
    const reference = document.createElement("span");
    reference.className = "identifier-value";
    reference.textContent = "earlier-fixture-row";
    link.append(reference);
    cell.replaceChildren(link);
  });
  await page.addStyleTag({ content: ".data-table tr { block-size: 120px }" });
  expect(await geometryFailures(page, false, "current-fixture")).not.toContain("G5");
  await result.locator(".identifier-value").evaluate((reference) => {
    reference.textContent = "~current-fixture";
  });
  expect(await geometryFailures(page, false, "current-fixture")).toContain("G5");
});

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
  await page.locator('input[type="search"]').fill("W".repeat(32));
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
    id: "G5c",
    body: `<div class="data-table"><table><tbody><tr><td>
      <div class="chip-list"><span class="chip">No next action</span></div>
      </td></tr></tbody></table></div>`,
    css: ".chip-list { width: 18rem } .chip { display: inline-block; width: 8rem }",
    defect: ".chip { width: 3rem }",
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
    body: `<div class="subject-header-facts"><dl>
      <div><dt>Claimed amount</dt><dd>100 EUR</dd></div><div><dt>Last updated</dt><dd>Monday</dd></div>
      <div><dt>State</dt><dd>Open</dd></div><div><dt>Deadline</dt><dd>Friday</dd></div>
      </dl></div>`,
    css: "dl { display: grid; grid-template-columns: repeat(2, max-content 10rem) } dl > div { grid-column: span 2; display: grid; grid-template-columns: subgrid } dd { margin: 0 }",
    defect: "dl > div { grid-template-columns: max-content 1fr }",
  },
  {
    id: "G21",
    body: `<section class="case-header-card"><section class="stages">Case stages</section></section>`,
    css: ".case-header-card { width: 500px; border: 1px solid; padding: 16px } .stages { height: 80px }",
    defect: ".stages { border: 1px solid }",
  },
  {
    id: "G23",
    body: `<div class="text-field"><div class="control-box"><input aria-label="Filter"></div></div>`,
    css: ".control-box:focus-within { outline: 2px solid } input { outline: 0 }",
    defect: ".text-field:focus-within { outline: 2px solid }",
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
  {
    id: "G31",
    body: `<div class="data-table"><table><tbody><tr>
      <td data-prominence="PRIMARY"><div class="data-table-cell">Claimant</div></td>
      <td data-prominence="PRIMARY"><div class="data-table-cell">Synthetic Party</div></td>
      <td><span class="data-table-menu"><button>More</button></span></td>
      </tr></tbody></table></div>`,
    css: "table { --table-layout: narrow; width: 360px; display: block } tbody { display: block } tr { position: relative; display: flex; flex-wrap: wrap; gap: 4px 12px; padding: 8px 56px 8px 8px } td { display: contents } .data-table-menu { position: absolute; top: 4px; right: 8px } button { height: 24px }",
    defect: ".data-table-cell { flex-basis: 100% }",
  },
  {
    id: "G32",
    body: `<div class="control-box"><span role="spinbutton" tabindex="0">Day</span> <span role="spinbutton" tabindex="0">Month</span></div>`,
    css: ".control-box:focus-within { outline: 2px solid } [role=spinbutton]:focus { background: Highlight; color: HighlightText; outline: 0 }",
    defect: "[role=spinbutton]:focus { background: transparent; color: inherit }",
    focus: "[role=spinbutton]:first-child",
  },
  {
    id: "G33",
    body: `<div class="control-box money-field"><input><div class="compact-picker">EUR</div></div>`,
    css: ".money-field { display: flex; height: 40px } .compact-picker { border-inline-start: 1px solid }",
    defect: ".compact-picker { border-inline-start: 0 }",
  },
  {
    id: "G34",
    body: `<div class="text-field"><div class="radio-options">Direction</div><p class="field-description">Choose how it was paid.</p></div>`,
    css: ".text-field { --space-1: 4px; display: grid; gap: var(--space-1) } p { margin: 0 }",
    defect: "p { margin-block-start: 1em }",
  },
  {
    id: "G35",
    body: `<ol class="case-stage-list">
      <li><span class="stage-connector" aria-hidden="true"></span><strong>Opened</strong></li>
      <li><span class="stage-connector" aria-hidden="true"></span><strong>Paid</strong><div class="money-meter"><meter value="1"></meter></div></li>
      </ol>`,
    css: "ol { display: flex; align-items: start } li { display: grid; margin-inline: 1rem } .stage-connector { height: 4px }",
    defect: "li:first-child strong { margin-block-start: 6px }",
  },
  {
    id: "G36",
    body: `<header class="phone-app-bar"><span>TEST-REFERENCE / 2026 / PROPERTY / SYNTHETIC / LIMIT / A-1234567 / CLAIM</span></header>
      <div class="case-workspace"><bdi class="identifier-value">TEST-REFERENCE / 2026 / PROPERTY / SYNTHETIC / LIMIT / A-1234567 / CLAIM</bdi></div>`,
    css: ".phone-app-bar { --shell-top-height: 56px; min-height: var(--shell-top-height); width: 100px } .phone-app-bar > span { display: block; white-space: nowrap; overflow: hidden; text-overflow: ellipsis }",
    defect: ".phone-app-bar > span { white-space: normal }",
  },
] as const;

const witnessWidths: Partial<Record<GeometryId, number>> = { G5b: 1440, G36: 390 };

for (const witness of anatomyWitnesses) {
  test(`${witness.id} rejects its anatomy regression`, async ({ page }) => {
    const width = witnessWidths[witness.id] ?? 1280;
    await page.setViewportSize({ width, height: 900 });
    await page.setContent(
      `<!doctype html><style>${witness.css}</style><main>${witness.body}</main>`,
    );
    if ("focus" in witness) {
      await page.locator(witness.focus).focus();
    }
    const failures = "focus" in witness ? focusFailures : geometryFailures;
    expect(await failures(page)).not.toContain(witness.id);
    await page.addStyleTag({ content: witness.defect });
    expect(await failures(page)).toContain(witness.id);
  });
}

test("G35 rejects a decorative meter alongside the paid amount", async ({ page }) => {
  await page.setContent(`<!doctype html><main><ol class="case-stage-list">
    <li><span class="stage-connector" aria-hidden="true"></span><strong>Paid</strong>
      <div class="money-meter"><meter value="1"></meter></div>
    </li></ol></main>`);
  expect(await geometryFailures(page)).not.toContain("G35");
  await page.locator(".case-stage-list li").evaluate((step) => {
    step.append(document.createElement("meter"));
  });
  expect(await geometryFailures(page)).toContain("G35");
});

const cardMoneyAndMenu = `
  <div class="data-table"><table><tbody><tr>
    <td data-prominence="PRIMARY" data-money><div class="data-table-cell">35.00 EUR</div></td>
    <td><span class="data-table-menu"><button>More</button></span></td>
  </tr></tbody></table></div>
`;
for (const [issue, defect] of [
  ["a menu lower than the first line", ".data-table-menu { top: 14px }"],
  ["a menu over the value", ".data-table-menu { left: 8px; right: auto }"],
  ["end-aligned money", ".data-table-cell { text-align: end }"],
] as const) {
  test(`G31 rejects ${issue}`, async ({ page }) => {
    await page.setContent(`<!doctype html><style>
      table { --table-layout: narrow; width: 360px; display: block }
      tbody { display: block }
      tr { position: relative; display: flex; gap: 12px; padding: 8px 56px 8px 8px }
      td { display: contents }
      .data-table-cell { width: 140px }
      .data-table-menu { position: absolute; top: 4px; right: 8px }
      button { height: 24px }
    </style><main>${cardMoneyAndMenu}</main>`);
    expect(await geometryFailures(page)).not.toContain("G31");
    await page.addStyleTag({ content: defect });
    expect(await geometryFailures(page)).toContain("G31");
  });
}

test("G31 gives a wrapped value and its successor their own lines", async ({ page }) => {
  await page.setContent(`<!doctype html><style>
    table { --table-layout: narrow; width: 290px; display: block }
    tbody { display: block }
    tr { display: flex; flex-wrap: wrap; gap: 4px 12px }
    td { display: contents }
    .long-value { flex-basis: 100% }
  </style><main><div class="data-table"><table><tbody><tr>
    <td data-prominence="PRIMARY"><div class="data-table-cell">Role</div></td>
    <td data-prominence="PRIMARY"><div class="data-table-cell long-value">
      A synthetic claimant name with enough words to wrap across more than one line
    </div></td>
    <td data-prominence="PRIMARY"><div class="data-table-cell">Current</div></td>
  </tr></tbody></table></div></main>`);
  expect(await geometryFailures(page)).not.toContain("G31");
  await page.addStyleTag({ content: ".long-value { flex-basis: 140px }" });
  expect(await geometryFailures(page)).toContain("G31");
});

test("G36 requires the full case reference outside the phone app bar", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 900 });
  await page.setContent(`<!doctype html>
    <style>.phone-app-bar { --shell-top-height: 56px; height: var(--shell-top-height) }</style>
    <header class="phone-app-bar"><span>TEST-REFERENCE</span></header>
    <main class="case-workspace"><bdi class="identifier-value">TEST-REFERENCE</bdi></main>
  `);
  expect(await geometryFailures(page)).not.toContain("G36");
  await page.addStyleTag({ content: ".identifier-value { display: none }" });
  expect(await geometryFailures(page)).toContain("G36");
});
