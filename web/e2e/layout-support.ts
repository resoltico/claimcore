import type { Locator, Page } from "@playwright/test";

/** Compare the control with the layout edges of the viewport and its rectangular scrollports. */
export const clippedControlEdges = (control: Locator, includeOutline = false): Promise<string[]> =>
  control.evaluate((element, paint) => {
    const bounds = element.getBoundingClientRect();
    const controlStyle = getComputedStyle(element);
    const extent =
      paint && controlStyle.outlineStyle !== "none"
        ? parseFloat(controlStyle.outlineWidth) +
          Math.max(0, parseFloat(controlStyle.outlineOffset))
        : 0;
    const rect = {
      left: bounds.left - extent,
      right: bounds.right + extent,
      top: bounds.top - extent,
      bottom: bounds.bottom + extent,
    };
    const clipped: string[] = [];
    const check = (source: string, edge: string, outside: boolean) => {
      if (outside) {
        clipped.push(`${source}: ${edge}`);
      }
    };
    const viewport = document.documentElement;
    check("viewport", "left", rect.left < 0);
    check("viewport", "top", rect.top < 0);
    check("viewport", "right", rect.right > viewport.clientWidth);
    check("viewport", "bottom", rect.bottom > viewport.clientHeight);

    const scrollport = (
      start: number,
      end: number,
      startBorder: string,
      endBorder: string,
      clientStart: number,
      clientSize: number,
    ) => {
      const border = parseFloat(startBorder);
      const paddingStart = start + border;
      const paddingEnd = end - parseFloat(endBorder);
      // The client metrics are integers. Use them to locate scrollbars, then keep
      // the fractional padding-box edges for the actual containment check.
      const startScrollbar = Math.max(0, clientStart - Math.round(border));
      const endScrollbar = Math.max(
        0,
        Math.round(paddingEnd - paddingStart) - clientSize - startScrollbar,
      );
      return [paddingStart + startScrollbar, paddingEnd - endScrollbar] as const;
    };

    for (
      let parent = element.parentElement;
      parent !== null && parent !== viewport;
      parent = parent.parentElement
    ) {
      const style = getComputedStyle(parent);
      const box = parent.getBoundingClientRect();
      const source = parent.classList.item(0) ?? parent.tagName.toLowerCase();
      if (style.overflowX !== "visible") {
        const [left, right] = scrollport(
          box.left,
          box.right,
          style.borderLeftWidth,
          style.borderRightWidth,
          parent.clientLeft,
          parent.clientWidth,
        );
        check(source, "left", rect.left < left);
        check(source, "right", rect.right > right);
      }
      if (style.overflowY !== "visible") {
        const [top, bottom] = scrollport(
          box.top,
          box.bottom,
          style.borderTopWidth,
          style.borderBottomWidth,
          parent.clientTop,
          parent.clientHeight,
        );
        check(source, "top", rect.top < top);
        check(source, "bottom", rect.bottom > bottom);
      }
    }
    return clipped;
  }, includeOutline);

type LayoutStage = "login" | "cases" | "narrow" | "zoom";

/** Reflow checks the document; two-dimensional tables keep their own scroll container. */
export const noHorizontalOverflow = async (
  page: Pick<Page, "evaluate">,
  stage: LayoutStage,
): Promise<void> => {
  const { scrollWidth, clientWidth } = await page.evaluate(() => ({
    clientWidth: document.documentElement.clientWidth,
    scrollWidth: document.documentElement.scrollWidth,
  }));
  if (scrollWidth > clientWidth + 1) {
    throw new Error(`E2E_LAYOUT_${stage.toUpperCase()}_OVERFLOW`);
  }
};
