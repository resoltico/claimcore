import type { Page } from "@playwright/test";

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
    const outside = await page.evaluate(() => {
      const width = document.documentElement.clientWidth;
      return [...document.querySelectorAll("body *")]
        .flatMap((element) => {
          const { left, right, width: size } = element.getBoundingClientRect();
          const excess = Math.max(-left, right - width);
          return size > 0 && excess > 1
            ? [{ tag: element.localName, className: element.getAttribute("class"), excess }]
            : [];
        })
        .toSorted((a, b) => b.excess - a.excess)
        .slice(0, 6);
    });
    throw new Error(
      `E2E_LAYOUT_${stage.toUpperCase()}_OVERFLOW ${scrollWidth}/${clientWidth} ${JSON.stringify(outside)}`,
    );
  }
};
