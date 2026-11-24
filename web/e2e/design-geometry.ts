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
  | "G16"
  | "G17"
  | "G18"
  | "G19"
  | "G20"
  | "G21"
  | "G22"
  | "G5b"
  | "G5c"
  | "G19b"
  | "G19c"
  | "G20b"
  | "G23"
  | "G24"
  | "G25"
  | "G26"
  | "G27"
  | "G28"
  | "G30"
  | "G31"
  | "G32"
  | "G33"
  | "G34"
  | "G35"
  | "G36";

type GeometryOptions = { limits?: boolean; caseMarker?: string | undefined };

export const geometryReport = (page: Page, options: GeometryOptions = {}) =>
  page.evaluate((frame) => {
    const { limits = false, caseMarker } = frame;
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
    const fixtureRows = (table: Element) =>
      [...table.querySelectorAll("tbody tr:not(.table-group)")].filter(
        (row) =>
          visible(row) &&
          (caseMarker === undefined ||
            table.closest(".worklist") === null ||
            row.querySelector("a .identifier-value")?.textContent.includes(caseMarker) === true),
      );
    const inflatedRows = (table: HTMLElement) =>
      fixtureRows(table).some((row) => row.getBoundingClientRect().height > 72);
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
          control.closest(".data-table,[role=listbox]") === null &&
          (control.matches(".control-box") || control.closest(".control-box") === null),
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
    const surfaces = [...document.querySelectorAll("main, [role=dialog]")];
    const lineRects = (range: Range) =>
      [...range.getClientRects()].filter((rect) => rect.width > 0);
    const multipleLines = (rects: DOMRect[]) =>
      new Set(rects.map(({ top }) => Math.round(top))).size > 1;
    const contained = (outer: DOMRect, inner: DOMRect) =>
      inner.left >= outer.left - 1 && inner.right <= outer.right + 1;
    const isOverflowRegion = (element: Element) =>
      element.matches(".data-table, .timeline-layout") ||
      (!element.matches("input, textarea") &&
        /auto|scroll|hidden|clip/u.test(getComputedStyle(element).overflowX));
    const overflowingRegions = surfaces.flatMap((surface) =>
      [surface, ...surface.querySelectorAll("*")]
        .filter(
          (element) =>
            visible(element) &&
            element.closest(".view-tabs-bar") === null &&
            isOverflowRegion(element) &&
            element.clientWidth > 2 &&
            element.scrollWidth > element.clientWidth + 1,
        )
        .map((element) => ({
          region: `${element.tagName.toLowerCase()}.${[...element.classList].join(".")}`,
          client: element.clientWidth,
          scroll: element.scrollWidth,
        })),
    );
    const pageSpill = document.documentElement.scrollWidth - innerWidth;
    const overflow = pageSpill > 1 || overflowingRegions.length > 0;
    const rowControlsOutside = [...document.querySelectorAll(".data-table, .timeline-layout")].some(
      (table) =>
        [...table.querySelectorAll("tbody button, .timeline-details")].some(
          (button) =>
            visible(button) &&
            !contained(table.getBoundingClientRect(), button.getBoundingClientRect()),
        ),
    );

    const range = document.createRange();
    const firstInlineContainer = (element: Element) => {
      let box = element;
      while (getComputedStyle(box).display === "inline" && box.parentElement !== null) {
        box = box.parentElement;
      }
      const style = getComputedStyle(box);
      return box.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight);
    };
    const wordSplits = () => {
      const measure = document.createElement("canvas").getContext("2d")!;
      const splits: string[] = [];
      const visited = new Set<Node>();
      const inspectText = (node: Node) => {
        const parent = node.parentElement;
        if (
          !(node instanceof Text) ||
          parent === null ||
          !visible(parent) ||
          parent.closest(".identifier-value") !== null ||
          visited.has(node)
        ) {
          return;
        }
        visited.add(node);
        const width = firstInlineContainer(parent);
        const style = getComputedStyle(parent);
        measure.font = `${style.fontStyle} ${style.fontWeight} ${style.fontSize} ${style.fontFamily}`;
        for (const match of node.data.matchAll(/[^\s\-‐–—/_.@:]+/gu)) {
          range.setStart(node, match.index);
          range.setEnd(node, match.index + match[0].length);
          if (multipleLines(lineRects(range)) && measure.measureText(match[0]).width <= width + 1) {
            splits.push(match[0].slice(0, 50));
          }
        }
      };
      for (const element of document.querySelectorAll(
        "main :is(td, th, dt, dd, p, li, h1, h2, h3), .chip, button, [role=tab], nav a",
      )) {
        if (!visible(element)) {
          continue;
        }
        const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
        for (let text = walker.nextNode(); text !== null; text = walker.nextNode()) {
          inspectText(text);
        }
      }
      return splits;
    };
    const splitWords = wordSplits();
    const identifiersOut = [...document.querySelectorAll(".identifier-value:not(input)")].some(
      (id) => {
        if (!visible(id)) {
          return false;
        }
        range.selectNodeContents(id);
        const rects = lineRects(range);
        if (Array.from(id.textContent.trim()).length <= 16 && multipleLines(rects)) {
          return true;
        }
        const cell = id.closest(".data-table-value");
        return (
          cell !== null && rects.some((rect) => !contained(cell.getBoundingClientRect(), rect))
        );
      },
    );

    const textBoxes = (element: Element) => {
      const boxes: DOMRect[] = [];
      const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
      for (let node = walker.nextNode(); node !== null; node = walker.nextNode()) {
        const parent = node.parentElement;
        if (parent !== null && visible(parent) && (node.textContent ?? "").trim() !== "") {
          range.selectNodeContents(node);
          boxes.push(...lineRects(range));
        }
      }
      return boxes;
    };
    const firstLine = (element: Element) => {
      const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
      for (let text = walker.nextNode(); text !== null; text = walker.nextNode()) {
        if (
          text instanceof Text &&
          text.data.trim() &&
          text.parentElement &&
          visible(text.parentElement)
        ) {
          range.selectNodeContents(text);
          return lineRects(range)[0];
        }
      }
      return undefined;
    };
    const labelsMisaligned = [
      ...document.querySelectorAll("main dl > div, [role=dialog] dl > div"),
    ].some((pair) => {
      const label = pair.querySelector(":scope > dt");
      const value = pair.querySelector(":scope > dd");
      if (label === null || value === null || !visible(label) || !visible(value)) {
        return false;
      }
      const a = label.getBoundingClientRect();
      const b = value.getBoundingClientRect();
      if (a.right > b.left + 1 && b.right > a.left + 1) {
        return false;
      }
      const labelLine = firstLine(label);
      const valueLine = firstLine(value);
      return (
        labelLine !== undefined &&
        valueLine !== undefined &&
        Math.abs(labelLine.bottom - valueLine.bottom) > 2
      );
    });

    const drawers = [...document.querySelectorAll(".drawer")].filter(visible);
    const border = (element: Element) => {
      const style = getComputedStyle(element);
      return [
        parseFloat(style.borderTopWidth),
        parseFloat(style.borderRightWidth),
        parseFloat(style.borderBottomWidth),
        parseFloat(style.borderLeftWidth),
      ] as const;
    };
    const wrongFields = drawers.some((drawer) => {
      const badButton = [...drawer.querySelectorAll(".control-box button")]
        .filter(visible)
        .some((button) => {
          const parent = button.closest(".control-box")!;
          const box = parent.getBoundingClientRect();
          const control = button.getBoundingClientRect();
          const end =
            getComputedStyle(parent).direction === "rtl"
              ? control.left - box.left
              : box.right - control.right;
          return border(button).some((width) => width > 0) || end > 8 || end < -1;
        });
      const gap =
        parseFloat(getComputedStyle(document.documentElement).getPropertyValue("--space-5")) *
        parseFloat(getComputedStyle(document.documentElement).fontSize);
      const badGap = [...drawer.querySelectorAll(".task-field-grid")].some((grid) => {
        const fields = [...grid.children]
          .filter(visible)
          .map((field) => field.getBoundingClientRect());
        return fields.some((field, index) => {
          const above = fields
            .slice(0, index)
            .filter(
              (peer) =>
                peer.top < field.top - 1 &&
                peer.left < field.right - 1 &&
                field.left < peer.right - 1,
            )
            .at(-1);
          return above !== undefined && Math.abs(field.top - above.bottom - gap) > 1;
        });
      });
      const groupGap = [...drawer.querySelectorAll(".task-field-section + .task-field-grid")]
        .filter(visible)
        .some(
          (grid) =>
            Math.abs(
              grid.getBoundingClientRect().top -
                grid.previousElementSibling!.getBoundingClientRect().bottom -
                gap,
            ) > 1,
        );
      return badButton || badGap || groupGap;
    });

    const crowdedDrawer = drawers.some((drawer) => {
      const subject = drawer.querySelector(".case-title-line .identifier-value")?.textContent;
      const duplicate =
        subject !== undefined &&
        [...drawer.querySelectorAll(".identifier-value")].filter(
          (element) => visible(element) && element.textContent === subject,
        ).length > 1;
      const { width } = drawer.getBoundingClientRect();
      const card = [
        ...drawer.querySelectorAll(".ui-dialog-body :is(div, section, form, fieldset)"),
      ].some(
        (element) =>
          visible(element) &&
          element.closest(".control-box") === null &&
          element.getBoundingClientRect().width > width * 0.6 &&
          border(element).every((side) => side >= 1),
      );
      const firstInput = [
        ...drawer.querySelectorAll(
          "input:not([type=hidden]), textarea, [role=spinbutton], [role=combobox], [role=radio]",
        ),
      ].find(visible);
      const late =
        !limits &&
        innerWidth === 390 &&
        firstInput !== undefined &&
        firstInput.getBoundingClientRect().top > innerHeight * 0.6;
      return duplicate || card || late;
    });
    const markers = [...document.querySelectorAll("main li, [role=dialog] li")].some((item) => {
      const style = getComputedStyle(item);
      return (
        visible(item) &&
        style.display === "list-item" &&
        style.listStyleType !== "none" &&
        item.closest('[data-kind="NARRATIVE"], .note-text') === null
      );
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
    const titleHeading =
      location.pathname === "/cases/new"
        ? document.querySelector('[role="dialog"] .ui-dialog-title')
        : mainHeading;
    const firstRow = [
      ...document.querySelectorAll(
        ".workspace-main :is(.worklist, .reminder-queue) .data-table tbody > tr:not(.table-group)",
      ),
    ].find(visible);
    const anatomy = () => {
      const rect = (element: Element) => element.getBoundingClientRect();
      const withinBox = (box: Element, control: Element) => {
        const [top, right, bottom, left] = border(box);
        const outer = rect(box);
        const inner = rect(control);
        return (
          inner.top >= outer.top + top - 0.5 &&
          inner.right <= outer.right - right + 0.5 &&
          inner.bottom <= outer.bottom - bottom + 0.5 &&
          inner.left >= outer.left + left - 0.5
        );
      };
      const boxes = [
        ...document.querySelectorAll("main .control-box, [role=dialog] .control-box"),
      ].filter(visible);
      const spillsControl = boxes.some((box) =>
        [...box.querySelectorAll('input, button, [role="spinbutton"], [role="group"]')]
          .filter(visible)
          .some(
            (control) =>
              !withinBox(box, control) ||
              (control.matches("button") &&
                (border(control).some((side) => side > 0) ||
                  getComputedStyle(control).backgroundColor !== "rgba(0, 0, 0, 0)")),
          ),
      );
      const misalignedFields = [...document.querySelectorAll(".task-field-grid")].some((grid) => {
        const fields = [...grid.children].filter(visible);
        const part = (field: Element, selector: string) =>
          [...field.querySelectorAll(selector)].find(
            (candidate) =>
              visible(candidate) && !candidate.parentElement?.closest(".field-label-row, label"),
          );
        return fields.some((field, index) =>
          fields.slice(index + 1).some((peer) => {
            if (Math.abs(rect(field).top - rect(peer).top) > 1) {
              return false;
            }
            return [
              ".field-label-row, legend",
              ".control-box, .radio-options, textarea",
              ".field-hint, .field-description",
            ].some((selector) => {
              const left = part(field, selector);
              const right = part(peer, selector);
              return (
                left !== undefined &&
                right !== undefined &&
                Math.abs(rect(left).top - rect(right).top) > 1
              );
            });
          }),
        );
      });
      const raggedFacts = [...document.querySelectorAll(".subject-header-facts")]
        .filter(visible)
        .some((facts) => {
          const pairs = [...facts.querySelectorAll("dl > div")].filter(visible);
          const rtl = getComputedStyle(facts).direction === "rtl";
          const columns = new Map<number, number[]>();
          for (const pair of pairs) {
            const value = pair.querySelector("dd");
            if (value === null || !visible(value)) {
              continue;
            }
            const key = Math.round(rtl ? rect(pair).right : rect(pair).left);
            const edge = rtl ? rect(value).right : rect(value).left;
            columns.set(key, [...(columns.get(key) ?? []), edge]);
          }
          const wraps = [...facts.querySelectorAll("dt")].some((label) => {
            range.selectNodeContents(label);
            return multipleLines(lineRects(range));
          });
          return wraps || [...columns.values()].some(unequalCentres);
        });
      const wideCases = [...document.querySelectorAll(".worklist .data-table table")].filter(
        (table) =>
          visible(table) &&
          getComputedStyle(table).getPropertyValue("--table-layout").trim() === "wide",
      );
      const caseChipsWrap =
        innerWidth === 1440 &&
        !limits &&
        wideCases.some((table) =>
          fixtureRows(table).some((row) =>
            [...row.querySelectorAll(".chip-list")].some(
              (chips) =>
                new Set(
                  [...chips.querySelectorAll(".chip")]
                    .filter(visible)
                    .map((chip) => Math.round(rect(chip).top)),
                ).size > 1,
            ),
          ),
        );
      const rows = [...document.querySelectorAll(".data-table tbody tr:not(.table-group)")].filter(
        visible,
      );
      const sideBySideRows = rows.filter((row) => getComputedStyle(row).display === "table-row");
      const paintedInlineBounds = (cell: Element) => {
        const painted = [
          ...[...cell.querySelectorAll("img, svg, button, .person-avatar, .chip")]
            .filter(visible)
            .map(rect),
          ...textBoxes(cell),
        ];
        return painted.length === 0
          ? []
          : [
              {
                left: Math.min(...painted.map((part) => part.left)),
                right: Math.max(...painted.map((part) => part.right)),
              },
            ];
      };
      const tightCells = sideBySideRows.some((row) => {
        const cellBounds = [...row.querySelectorAll(":scope > td")]
          .filter(visible)
          .flatMap(paintedInlineBounds)
          .sort((a, b) => a.left - b.left);
        return cellBounds.some(
          (box, index) => index > 0 && box.left - cellBounds[index - 1]!.right < 11.5,
        );
      });
      const rowsMisaligned = sideBySideRows.some((row) => {
        const lines = [...row.querySelectorAll(":scope > td > .data-table-cell")]
          .filter(visible)
          .flatMap((cell) => firstLine(cell) ?? []);
        const bottoms = lines.map((line) => line.bottom);
        const [first] = lines.sort((a, b) => a.top - b.top);
        const textMisaligned =
          bottoms.length > 1 && Math.max(...bottoms) - Math.min(...bottoms) > 2;
        const offLine =
          first !== undefined &&
          [...row.querySelectorAll(":scope > .data-table-controls :is(button, .data-table-menu)")]
            .filter(visible)
            .some((control) => {
              const { top, height } = rect(control);
              const centre = top + height / 2;
              return centre < first.top - 4 || centre > first.bottom + 4;
            });
        return textMisaligned || offLine;
      });
      const cardMenuIssue = (menu: Element | null, lines: DOMRect[][]) => {
        const first = lines[0]?.[0];
        if (menu === null || first === undefined) {
          return false;
        }
        const menuBox = rect(menu);
        const offLine =
          Math.abs(menuBox.top + menuBox.height / 2 - (first.top + first.height / 2)) > 4;
        return (
          offLine ||
          lines
            .flat()
            .some(
              (box) =>
                box.left < menuBox.right - 0.5 &&
                menuBox.left + 0.5 < box.right &&
                box.top < menuBox.bottom - 0.5 &&
                menuBox.top + 0.5 < box.bottom,
            )
        );
      };
      const cardRowIssue = (row: Element) => {
        const style = getComputedStyle(row);
        const rtl = style.direction === "rtl";
        const start = (bounds: { left: number; right: number }) =>
          rtl ? -bounds.right : bounds.left;
        const end = (bounds: { left: number; right: number }) =>
          rtl ? -bounds.left : bounds.right;
        const contentStart = start(rect(row)) + parseFloat(style.paddingInlineStart);
        const contentEnd = end(rect(row)) - parseFloat(style.paddingInlineEnd);
        const gap = parseFloat(style.columnGap) || 0;
        const menu = row.querySelector(".data-table-menu button");
        const values = [
          ...row.querySelectorAll("td[data-prominence=PRIMARY] > .data-table-cell"),
        ].filter(visible);
        const lines = values.map(textBoxes);
        const oneLine = (line: DOMRect[]) =>
          line.length > 0 &&
          line.every((box) => box.top < line[0]!.bottom - 2 && box.bottom > line[0]!.top + 2);

        const idleSpace = values.some((value, index) => {
          const before = lines[index - 1] ?? [];
          const after = lines[index]!;
          const [previousLine] = before;
          const [nextLine] = after;
          if (previousLine === undefined || nextLine === undefined) {
            return false;
          }
          const sameLine =
            nextLine.top < previousLine.bottom - 2 && previousLine.top < nextLine.bottom - 2;
          if (!oneLine(before) || !oneLine(after)) {
            return sameLine;
          }
          if (sameLine) {
            return false;
          }
          const [previous] = paintedInlineBounds(values[index - 1]!);
          const [next] = paintedInlineBounds(value);
          return (
            previous !== undefined &&
            next !== undefined &&
            end(previous) + gap + next.right - next.left <= contentEnd - 1
          );
        });

        const paddedMoney = values.some((cell, index) => {
          const amountLines = lines[index]!;
          if (amountLines.length === 0 || cell.closest("td[data-money]") === null) {
            return false;
          }
          const top = Math.min(...amountLines.map((box) => box.top));
          const startOfLine = Math.min(
            ...amountLines.filter((box) => box.top < top + 2).map(start),
          );
          return Math.abs(startOfLine - start(rect(cell))) > 1;
        });
        const recommendation = row.querySelector(".data-table-recommended");
        const displacedAction = () => {
          if (recommendation === null || !visible(recommendation)) {
            return false;
          }
          const prior = [...values, ...row.querySelectorAll(".data-table-stack")].filter(visible);
          const bounds = rect(recommendation);
          return (
            Math.abs(start(bounds) - contentStart) > 1 ||
            prior.some((value) => rect(value).bottom > bounds.top + 1)
          );
        };
        return idleSpace || cardMenuIssue(menu, lines) || paddedMoney || displacedAction();
      };
      const brokenCard = rows
        .filter(
          (row) =>
            getComputedStyle(row.closest("table")!).getPropertyValue("--table-layout").trim() ===
            "narrow",
        )
        .some(cardRowIssue);
      const measure = document.createElement("canvas").getContext("2d")!;
      const splitChip = [...document.querySelectorAll("main .chip, [role=dialog] .chip")]
        .filter(visible)
        .some((chip) => {
          if (!multipleLines(textBoxes(chip))) {
            return false;
          }
          const table = chip.closest(".data-table table");
          if (
            table !== null &&
            getComputedStyle(table).getPropertyValue("--table-layout").trim() !== "narrow"
          ) {
            return true;
          }
          const list = chip.closest(".chip-list") ?? chip.parentElement;
          const style = getComputedStyle(chip);
          measure.font = `${style.fontStyle} ${style.fontWeight} ${style.fontSize} ${style.fontFamily}`;
          const occupied =
            measure.measureText(chip.textContent.trim()).width +
            parseFloat(style.paddingInlineStart) +
            parseFloat(style.paddingInlineEnd) +
            parseFloat(style.borderInlineStartWidth) +
            parseFloat(style.borderInlineEndWidth);
          return list !== null && occupied <= list.clientWidth;
        });

      const cards = [
        ...document.querySelectorAll("main .section-card, main .case-header-card"),
      ].filter(visible);
      const nestedCard = cards.some((card) =>
        [...card.querySelectorAll("div, section, ol, ul, table")]
          .filter(visible)
          .some(
            (child) =>
              child.closest(".control-box, .chip, button") === null &&
              rect(child).width > rect(card).width * 0.6 &&
              rect(child).height > 60 &&
              getComputedStyle(child).borderTopStyle === "solid" &&
              border(child).every((side) => side >= 1),
          ),
      );
      const squeezedEntries = cards.some((card) => {
        const entries = [...card.querySelectorAll(".entries-view dl > div")]
          .filter(visible)
          .map(rect);
        return entries.some((entry) =>
          entries.some(
            (above) =>
              above.top < entry.top - 1 &&
              above.bottom <= entry.top + 1 &&
              above.right > entry.left + 1 &&
              entry.right > above.left + 1 &&
              entry.top - above.bottom < 11.5,
          ),
        );
      });
      const doubleRules = cards.some((card) => {
        const rules = [...card.querySelectorAll("*")]
          .filter(visible)
          .flatMap((element) => {
            if (
              element.closest(".control-box,input,textarea,button,.chip,meter,[role=tablist]") !==
              null
            ) {
              return [];
            }
            const css = getComputedStyle(element);
            const [top, right, bottom, left] = border(element);
            if (left > 0 || right > 0) {
              return [];
            }
            const r = rect(element);
            const edges = [];
            if (top > 0 && css.borderTopStyle === "solid") {
              edges.push({ r, y: r.top });
            }
            if (bottom > 0 && css.borderBottomStyle === "solid") {
              edges.push({ r, y: r.bottom });
            }
            return edges;
          })
          .sort((a, b) => a.y - b.y);
        const visual = [...card.querySelectorAll("button,input,textarea,a,[role=combobox]")]
          .filter(visible)
          .map(rect);
        const walker = document.createTreeWalker(card, NodeFilter.SHOW_TEXT);
        for (let node = walker.nextNode(); node !== null; node = walker.nextNode()) {
          if (
            (node.textContent ?? "").trim() !== "" &&
            node.parentElement !== null &&
            visible(node.parentElement)
          ) {
            range.selectNodeContents(node);
            visual.push(...lineRects(range));
          }
        }
        return rules.some(({ r: a, y }, index) =>
          rules.slice(index + 1).some(({ r: b, y: end }) => {
            const left = Math.max(a.left, b.left);
            const right = Math.min(a.right, b.right);
            return (
              end - y <= 32 &&
              right - left > Math.min(a.width, b.width) / 2 &&
              !visual.some(
                (box) =>
                  box.bottom > y + 1 && box.top < end - 1 && box.left < right && box.right > left,
              )
            );
          }),
        );
      });
      const floatingCards = [...document.querySelectorAll(".case-workspace")].some((workspace) => {
        const next = workspace.querySelector(".case-area-next");
        const claim = workspace.querySelector(".case-area-claim");
        if (
          next === null ||
          claim === null ||
          !visible(next) ||
          !visible(claim) ||
          Math.abs(rect(next).left - rect(claim).left) < 2
        ) {
          return false;
        }
        const gap = parseFloat(getComputedStyle(workspace).rowGap);
        const side = [
          ...workspace.querySelectorAll(".case-area-next, .case-area-work, .case-area-related"),
        ]
          .filter(visible)
          .map(rect)
          .sort((a, b) => a.top - b.top);
        return side.some(
          (card, index) => index > 0 && Math.abs(card.top - side[index - 1]!.bottom - gap) > 1,
        );
      });
      const splitToolbar =
        innerWidth === 1280 &&
        [...document.querySelectorAll(".worklist-toolbar .toolbar-actions")].some((group) => {
          const buttons = [...group.querySelectorAll("button, a")].filter(visible).map(rect);
          return (
            buttons.length > 1 &&
            Math.max(...buttons.map(({ top }) => top)) -
              Math.min(...buttons.map(({ top }) => top)) >
              1
          );
        });
      const canvas = document.createElement("canvas").getContext("2d")!;
      const color = (value: string) => {
        canvas.fillStyle = value;
        return canvas.fillStyle;
      };
      const doubleAccent = [...document.querySelectorAll("[role=dialog]")]
        .filter(visible)
        .some((dialog) => {
          const accent = color(getComputedStyle(dialog).getPropertyValue("--accent-solid").trim());
          const solid = [...dialog.querySelectorAll("button, a, .radio-option > label")].filter(
            (candidate) =>
              visible(candidate) && color(getComputedStyle(candidate).backgroundColor) === accent,
          );
          return (
            solid.length > 1 || solid.some((element) => element.matches(".radio-option > label"))
          );
        });
      const missingDivider = boxes
        .filter((box) => box.matches(".money-field"))
        .some((box) => {
          const currency = box.querySelector(":scope > .compact-picker");
          if (currency === null) {
            return true;
          }
          const style = getComputedStyle(currency);
          return (
            parseFloat(style.borderInlineStartWidth) < 1 ||
            style.borderInlineStartStyle === "none" ||
            color(style.borderInlineStartColor) === color("transparent") ||
            rect(currency).height < 0.6 * rect(box).height
          );
        });
      const tokenSize = (element: Element, name: string) => {
        const token = getComputedStyle(element).getPropertyValue(name).trim();
        return (
          parseFloat(token) *
          (token.endsWith("rem")
            ? parseFloat(getComputedStyle(document.documentElement).fontSize)
            : 1)
        );
      };
      const misplacedHint = [...document.querySelectorAll(".text-field")]
        .filter(visible)
        .some((field) => {
          const hint = [...field.querySelectorAll(".field-hint, .field-description")].find(
            (element) =>
              visible(element) && element.closest(".field-label-row, legend, label") === null,
          );
          const control = [...field.querySelectorAll(".control-box, .radio-options, textarea")]
            .filter(visible)
            .at(-1);
          return (
            hint !== undefined &&
            control !== undefined &&
            Math.abs(rect(hint).top - rect(control).bottom - tokenSize(field, "--space-1")) > 1
          );
        });
      const malformedStage = [...document.querySelectorAll(".case-stage-list")]
        .filter(visible)
        .some((list) => {
          const steps = [...list.querySelectorAll(":scope > li")].filter(visible);
          const stageRows = new Map<number, number[]>();
          for (const step of steps) {
            const label = step.querySelector(":scope > strong");
            if (
              label === null ||
              step.querySelector(':scope > .stage-connector[aria-hidden="true"]') === null
            ) {
              return true;
            }
            const line = Math.round(rect(step).top);
            stageRows.set(line, [...(stageRows.get(line) ?? []), rect(label).top]);
          }
          return (
            [...stageRows.values()].some(unequalCentres) ||
            [...list.querySelectorAll("meter, [role=meter], [role=progressbar]")].some(
              (meter) => meter.closest(".money-meter, [aria-hidden=true]") === null,
            )
          );
        });
      const bar = document.querySelector(".phone-app-bar");
      const malformedBar =
        innerWidth === 390 &&
        bar !== null &&
        visible(bar) &&
        (rect(bar).height > tokenSize(bar, "--shell-top-height") + 1 ||
          (document.querySelector(".case-workspace") !== null &&
            ![...document.querySelectorAll("main .identifier-value")].some(
              (value) =>
                visible(value) &&
                value.textContent.trim() === bar.querySelector(":scope > span")?.textContent.trim(),
            )));
      const checks: ReadonlyArray<readonly [GeometryId, boolean]> = [
        ["G5b", caseChipsWrap],
        ["G5c", splitChip],
        ["G19b", spillsControl],
        ["G19c", misalignedFields],
        ["G20b", raggedFacts],
        ["G24", tightCells],
        ["G25", rowsMisaligned],
        ["G26", squeezedEntries],
        ["G27", doubleRules],
        ["G28", floatingCards],
        ["G30", splitToolbar],
        ["G31", brokenCard],
        ["G33", missingDivider],
        ["G34", misplacedHint],
        ["G35", malformedStage],
        ["G36", malformedBar],
      ];
      return { checks, nestedCard, doubleAccent };
    };
    const { checks: anatomyChecks, nestedCard, doubleAccent } = anatomy();
    const findings: ReadonlyArray<readonly [GeometryId, boolean]> = [
      ["G1", headingDisplaced()],
      ["G2", footerClipped()],
      ["G3", [...document.querySelectorAll(".case-title-line .chip-list")].some(chipsMisaligned)],
      ["G4", overflow],
      [
        "G5",
        !limits &&
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
      ["G9", splitWords.length > 0],
      ["G10", primary.length > 1 || doubleAccent],
      ["G12", tinyText],
      [
        "G13",
        [...document.querySelectorAll(".workspace-main details")].some(
          (detail) => detail.closest('[role="dialog"], .about-panel') === null,
        ),
      ],
      ["G14", titleHeading !== null && !document.title.includes(titleHeading.textContent.trim())],
      [
        "G15",
        innerWidth < 640 &&
          firstRow !== undefined &&
          firstRow.getBoundingClientRect().top > innerHeight * 0.6,
      ],
      ["G17", identifiersOut],
      ["G18", rowControlsOutside],
      ["G19", wrongFields],
      ["G20", labelsMisaligned],
      ["G21", crowdedDrawer || nestedCard],
      ["G22", markers],
      ...anatomyChecks,
    ];
    return {
      violations: findings.flatMap(([id, violated]) => (violated ? [id] : [])),
      splitWords,
      overflowingRegions,
      pageSpill,
      firstRowTop: firstRow?.getBoundingClientRect().top,
    };
  }, options);

export const focusFailures = (page: Page) =>
  page.evaluate((): GeometryId[] => {
    const active = document.activeElement;
    if (active === null) {
      return [];
    }
    const box = active.closest(".control-box");
    if (box === null) {
      return [];
    }
    const field = box.closest(".text-field, .field-group") ?? box;
    let rings = 0;
    for (let element: Element | null = active; element !== null; element = element.parentElement) {
      const style = getComputedStyle(element);
      if (
        (style.outlineStyle !== "none" && parseFloat(style.outlineWidth) > 0) ||
        style.boxShadow !== "none"
      ) {
        rings++;
      }
      if (element === field) {
        break;
      }
    }
    const findings: GeometryId[] = rings === 1 ? [] : ["G23"];
    const focusIndistinct = () => {
      if (!active.matches('[role="spinbutton"]')) {
        return false;
      }
      const focused = getComputedStyle(active);
      return [...box.querySelectorAll('[role="spinbutton"]')]
        .filter((sibling) => sibling !== active)
        .some((sibling) => {
          const unfocused = getComputedStyle(sibling);
          return (
            focused.backgroundColor === unfocused.backgroundColor &&
            focused.color === unfocused.color
          );
        });
    };
    if (focusIndistinct()) {
      findings.push("G32");
    }
    return findings;
  });

export const geometryFailures = async (
  page: Page,
  limits = false,
  caseMarker?: string,
): Promise<GeometryId[]> => (await geometryReport(page, { limits, caseMarker })).violations;

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

export const tableLayoutState = (page: Page) =>
  page
    .locator("main .data-table table")
    .first()
    .evaluate((table) => ({
      layout: getComputedStyle(table).getPropertyValue("--table-layout").trim(),
      width: table.getBoundingClientRect().width,
    }));

export const isWideTable = (reading: Awaited<ReturnType<typeof tableLayoutState>>) =>
  reading.layout === "wide";
export const stableTableLayout = (
  before: Awaited<ReturnType<typeof tableLayoutState>>,
  after: Awaited<ReturnType<typeof tableLayoutState>>,
) => before.layout === after.layout && Math.abs(before.width - after.width) <= 0.5;
