import type { Page } from "@playwright/test";

export type GeometryId =
  | "G1"
  | "G2"
  | "G3"
  | "G4"
  | "G5"
  | "G6"
  | "G8"
  | "G9"
  | "G10"
  | "G11"
  | "G12"
  | "G13"
  | "G14"
  | "G15"
  | "G16";

export const geometryFailures = (page: Page): Promise<GeometryId[]> =>
  page.evaluate(() => {
    const visible = (element: Element) => {
      const rect = element.getBoundingClientRect();
      if (rect.width <= 1 || rect.height <= 1) {
        return false;
      }
      for (let parent: Element | null = element; parent !== null; parent = parent.parentElement) {
        const style = getComputedStyle(parent);
        if (style.clipPath !== "none" || style.visibility === "hidden") {
          return false;
        }
      }
      return true;
    };

    const headingDisplaced = () => {
      const main = document.querySelector(".workspace-main");
      const heading = main?.querySelector("h1");
      return (
        main !== null &&
        main.closest('[inert], [aria-hidden="true"]') === null &&
        heading !== undefined &&
        heading !== null &&
        visible(heading) &&
        heading.getBoundingClientRect().top - main.getBoundingClientRect().top > 48
      );
    };

    const footerClipped = () => {
      const footer = document.querySelector(".shell-footer");
      if (!matchMedia("(width >= 64rem)").matches || footer === null) {
        return false;
      }
      const rect = footer.getBoundingClientRect();
      return !visible(footer) || rect.top < 0 || rect.bottom > innerHeight;
    };

    const unequalCentres = (line: number[]) =>
      line.length > 1 && Math.max(...line) - Math.min(...line) > 1;
    const chipsMisaligned = (group: Element) => {
      const rtl = getComputedStyle(group).direction === "rtl";
      let line: number[] = [];
      let previousInline = -Infinity;
      for (const chip of [...group.querySelectorAll(".chip")].filter(visible)) {
        const point = chip.getBoundingClientRect();
        const inline = rtl ? -point.right : point.left;
        if (inline <= previousInline) {
          if (unequalCentres(line)) {
            return true;
          }
          line = [];
        }
        line.push(point.top + point.height / 2);
        previousInline = inline;
      }
      return unequalCentres(line);
    };

    const tables = [
      ...document.querySelectorAll<HTMLElement>(
        ":is(.worklist, .reminder-list, .people-listing) .data-table",
      ),
    ].filter(visible);
    const inflatedRows = (table: HTMLElement) =>
      [...table.querySelectorAll("tbody tr:not(.table-group)")].some(
        (row) => visible(row) && row.getBoundingClientRect().height > 72,
      );
    const extraRowActions = (table: HTMLElement) =>
      [...table.querySelectorAll(".data-table-actions")].some(
        (controls) =>
          [...controls.querySelectorAll("button:not([aria-haspopup])")].filter(visible).length > 1,
      );

    const targetRem = parseFloat(
      getComputedStyle(document.documentElement).getPropertyValue("--control-target"),
    );
    const target =
      (Number.isFinite(targetRem) ? targetRem : 2.5) *
      parseFloat(getComputedStyle(document.documentElement).fontSize);
    const controlGeometry = [
      ...document.querySelectorAll("main form, .toolbar, [role=dialog] form"),
    ].some((form) => {
      const controls = [
        ...form.querySelectorAll(
          ".control-box, button, input:not([type=hidden],[type=radio],[type=checkbox],[type=file])",
        ),
      ].filter(
        (control) =>
          visible(control) &&
          !control.matches("textarea") &&
          control.closest(".data-table,[role=listbox]") === null,
      );
      const bounds = controls.map((control) => control.getBoundingClientRect());
      return bounds.some(
        (rect, index) =>
          Math.abs(rect.height - target) > 1 ||
          bounds
            .slice(index + 1)
            .some((peer) =>
              rect.left < peer.right && peer.left < rect.right
                ? false
                : Math.abs(rect.top - peer.top) < target / 2 &&
                  Math.abs(rect.bottom - peer.bottom) > 1,
            ),
      );
    });
    const words = [
      ...document.querySelectorAll(
        "main td, main th, .chip, button, [role=tab], nav a, main h1, main h2, main h3",
      ),
    ];
    const wrap = words.filter(visible).some((element) => {
      const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
      const range = document.createRange();
      for (let text = walker.nextNode(); text !== null; text = walker.nextNode()) {
        if (
          !(text instanceof Text) ||
          text.parentElement === null ||
          !visible(text.parentElement) ||
          (innerWidth < 640 && text.parentElement.closest(".identifier-value") !== null)
        ) {
          continue;
        }
        for (const word of text.data.matchAll(/\S+/gu)) {
          range.setStart(text, word.index);
          range.setEnd(text, word.index + word[0].length);
          const lines = new Set(
            [...range.getClientRects()]
              .filter((rect) => rect.width > 0)
              .map((rect) => Math.round(rect.top)),
          );
          if (lines.size > 1) {
            return true;
          }
        }
      }
      return false;
    });
    const primary = [...document.querySelectorAll(".ui-button--primary")].filter(
      (element) =>
        visible(element) &&
        element.closest("[role=dialog]") === null &&
        !element.matches(':disabled,[data-disabled],[aria-disabled="true"]'),
    );
    const tinyText = [
      ...document.querySelectorAll(
        ".workspace-main *, .workspace-sidebar *, .workspace-mobile-tabs *",
      ),
    ].some(
      (element) =>
        visible(element) &&
        [...element.childNodes].some((node) => node instanceof Text && node.data.trim() !== "") &&
        parseFloat(getComputedStyle(element).fontSize) < 13,
    );
    const mainHeading = document.querySelector(".workspace-main h1");
    const firstRow = [
      ...document.querySelectorAll(
        ".workspace-main :is(.worklist, .reminder-queue) .data-table tbody > tr:not(.table-group)",
      ),
    ].find(visible);
    const findings: ReadonlyArray<readonly [GeometryId, boolean]> = [
      ["G1", headingDisplaced()],
      ["G2", footerClipped()],
      ["G3", [...document.querySelectorAll(".case-title-line .chip-list")].some(chipsMisaligned)],
      ["G4", tables.some((table) => table.scrollWidth > table.clientWidth)],
      [
        "G5",
        tables.some((table) => {
          const inner = table.querySelector("table");
          return (
            inner !== null &&
            getComputedStyle(inner).getPropertyValue("--table-layout").trim() === "wide" &&
            inflatedRows(table)
          );
        }),
      ],
      ["G6", tables.some(extraRowActions)],
      ["G8", controlGeometry],
      ["G9", wrap],
      ["G10", primary.length > 1],
      ["G12", tinyText],
      [
        "G13",
        [...document.querySelectorAll(".workspace-main details")].some(
          (detail) => detail.closest('[role="dialog"], .about-panel') === null,
        ),
      ],
      ["G14", mainHeading !== null && !document.title.includes(mainHeading.textContent.trim())],
      [
        "G15",
        innerWidth < 640 &&
          firstRow !== undefined &&
          firstRow.getBoundingClientRect().top > innerHeight * 0.6,
      ],
    ];
    return findings.flatMap(([id, violated]) => (violated ? [id] : []));
  });

export const narrowTableFailures = (page: Page): Promise<string[]> =>
  page.evaluate(async () => {
    await document.fonts.ready;
    const tables = [...document.querySelectorAll<HTMLElement>(".workspace-main .data-table")];
    const cells = tables.flatMap((table) => [
      ...table.querySelectorAll<HTMLElement>('td[data-prominence="PRIMARY"] > .data-table-cell'),
    ]);
    const clip = cells.some(
      (cell) =>
        cell.scrollWidth > cell.clientWidth + 1 || cell.scrollHeight > cell.clientHeight + 1,
    );
    const range = document.createRange();
    const crushed = cells.some((cell) =>
      [...cell.querySelectorAll(".plain-text-value")].some((text) => {
        const node = text.firstChild;
        if (node === null || !(node instanceof Text)) {
          return false;
        }
        for (const word of node.data.matchAll(
          /(?<![\p{L}\p{M}])[\p{L}\p{M}]{3,10}(?![\p{L}\p{M}])/gu,
        )) {
          range.setStart(node, word.index);
          range.setEnd(node, word.index + word[0].length);
          const lineTops = [...range.getClientRects()].map((rect) => rect.top);
          if (lineTops.length > 1 && Math.max(...lineTops) - Math.min(...lineTops) > 2) {
            return true;
          }
        }
        return false;
      }),
    );
    return [
      ...(tables.length === 0 ? ["missing-table"] : []),
      ...(tables.some((table) => table.scrollWidth > table.clientWidth + 1)
        ? ["table-overflow"]
        : []),
      ...(clip ? ["clipped-primary-cell"] : []),
      ...(crushed ? ["split-ordinary-word"] : []),
    ];
  });

export const headingOffset = (page: Page) =>
  page
    .locator(".workspace-main h1")
    .evaluate(
      (heading) =>
        heading.getBoundingClientRect().top - heading.closest("main")!.getBoundingClientRect().top,
    );

export const headingOffsetFailures = (offsets: ReadonlyArray<number>) =>
  offsets.length > 1 && Math.max(...offsets) - Math.min(...offsets) > 1 ? ["G11"] : [];

export const feedbackFailures = (page: Page, message: string) =>
  page.evaluate((expected) => {
    const banner = document.querySelector(".page > .status-banner");
    return banner !== null &&
      banner.contains(document.activeElement) &&
      banner.textContent.includes(expected)
      ? []
      : ["G16"];
  }, message);
