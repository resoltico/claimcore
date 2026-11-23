import type { Page } from "@playwright/test";

type LayoutStage = "login" | "cases" | "narrow" | "zoom";

/** Reflow checks the document; two-dimensional tables keep their own scroll container. */
export const noHorizontalOverflow = async (
  page: Pick<Page, "evaluate">,
  stage: LayoutStage,
): Promise<void> => {
  const bounds = await page.evaluate(() => {
    const { clientWidth, scrollWidth } = document.documentElement;
    const outside =
      scrollWidth <= clientWidth + 1
        ? []
        : [...document.querySelectorAll("main *, aside *, nav *, header *")]
            .map((node) => ({ node, x: node.getBoundingClientRect() }))
            .filter(({ x }) => x.width > 0 && (x.left < -1 || x.right > clientWidth + 1))
            .map(({ node, x }) => ({
              element: node.tagName,
              class: node.getAttribute("class"),
              left: Math.round(x.left),
              right: Math.round(x.right),
              clippedByTable: node.closest(".data-table") !== null,
            }))
            .sort(
              (a, b) =>
                Number(a.clippedByTable) - Number(b.clippedByTable) ||
                Math.max(b.right - clientWidth, -b.left) - Math.max(a.right - clientWidth, -a.left),
            )
            .slice(0, 10);
    return { clientWidth, scrollWidth, outside };
  });
  if (bounds.scrollWidth > bounds.clientWidth + 1) {
    throw new Error(`E2E_LAYOUT_${stage.toUpperCase()}_OVERFLOW ${JSON.stringify(bounds)}`);
  }
};
