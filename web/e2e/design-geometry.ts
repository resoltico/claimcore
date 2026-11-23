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
