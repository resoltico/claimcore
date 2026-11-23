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
    throw new Error(`E2E_LAYOUT_${stage.toUpperCase()}_OVERFLOW`);
  }
};
