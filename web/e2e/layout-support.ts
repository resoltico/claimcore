import type { Page } from "@playwright/test";

type LayoutStage = "login" | "cases" | "narrow" | "zoom";

/** Reflow checks the document; two-dimensional tables keep their own scroll container. */
export const noHorizontalOverflow = async (
  page: Pick<Page, "evaluate">,
  stage: LayoutStage,
): Promise<void> => {
  const geometry = await page.evaluate(() => {
    const { clientWidth, scrollWidth } = document.documentElement;
    const protrusions =
      scrollWidth <= clientWidth + 1
        ? []
        : [...document.querySelectorAll("body *")]
            .flatMap((element) => {
              const box = element.getBoundingClientRect();
              const spill = Math.max(-box.left, box.right - clientWidth);
              return box.width > 1 && box.height > 1 && spill > 1
                ? [{ tag: element.tagName, className: element.getAttribute("class"), spill }]
                : [];
            })
            .sort((left, right) => right.spill - left.spill)
            .slice(0, 6);
    return { clientWidth, scrollWidth, protrusions };
  });
  if (geometry.scrollWidth > geometry.clientWidth + 1) {
    throw new Error(
      `E2E_LAYOUT_${stage.toUpperCase()}_OVERFLOW ${geometry.scrollWidth - geometry.clientWidth}px ${JSON.stringify(geometry.protrusions)}`,
    );
  }
};
