import type { Page } from "@playwright/test";

export type GeometryId = "G1" | "G2" | "G3" | "G4" | "G5" | "G6" | "G7";

export const geometryFailures = (page: Page): Promise<GeometryId[]> =>
  page.evaluate(() => {
    const visible = (element: Element) => {
      const rect = element.getBoundingClientRect();
      return rect.width > 0 && rect.height > 0 && getComputedStyle(element).visibility !== "hidden";
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

    const findings: ReadonlyArray<readonly [GeometryId, boolean]> = [
      ["G1", headingDisplaced()],
      ["G2", footerClipped()],
      ["G3", [...document.querySelectorAll(".case-title-line .chip-list")].some(chipsMisaligned)],
      ["G4", innerWidth === 1440 && tables.some((table) => table.scrollWidth > table.clientWidth)],
      ["G5", innerWidth === 1440 && tables.some(inflatedRows)],
      ["G6", tables.some(extraRowActions)],
      [
        "G7",
        document.querySelector(
          ".legal-notice, .data-table tbody details, .case-detail details[data-region='DETAILS']",
        ) !== null,
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
