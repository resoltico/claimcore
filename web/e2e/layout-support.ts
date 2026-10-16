import { writeFile } from "node:fs/promises";
import { test, type Page } from "@playwright/test";

const collectLayoutMetrics = () => {
  const { clientWidth } = document.documentElement;
  const offenders = [...document.querySelectorAll("*")]
    .map((element) => ({ element, rect: element.getBoundingClientRect() }))
    .filter(({ rect }) => rect.left < -1 || rect.right > clientWidth + 1)
    .slice(0, 5)
    .map(({ element, rect }) => ({
      tag: element.tagName.toLowerCase(),
      className: typeof element.className === "string" ? element.className : "",
      left: Math.round(rect.left),
      right: Math.round(rect.right),
      width: Math.round(rect.width),
    }));
  const scrollable = [...document.querySelectorAll("*")]
    .filter(
      (element) =>
        element !== document.documentElement &&
        element !== document.body &&
        element.scrollWidth > element.clientWidth + 1,
    )
    .sort((left, right) => right.scrollWidth - left.scrollWidth)
    .slice(0, 10)
    .map((element) => ({
      tag: element.tagName.toLowerCase(),
      className: typeof element.className === "string" ? element.className : "",
      clientWidth: element.clientWidth,
      scrollWidth: element.scrollWidth,
    }));
  return {
    clientWidth,
    scrollWidth: document.documentElement.scrollWidth,
    bodyWidth: Math.round(document.body.getBoundingClientRect().width),
    offenders,
    scrollable,
  };
};

type LayoutStage = "login" | "cases" | "narrow" | "zoom";
const safeReport = (stage: LayoutStage, metrics: ReturnType<typeof collectLayoutMetrics>) => ({
  stage,
  clientWidth: metrics.clientWidth,
  scrollWidth: metrics.scrollWidth,
  bodyWidth: metrics.bodyWidth,
  offenders: metrics.offenders.map(({ tag, className, left, right, width }) => ({
    tag,
    className: className.replace(/[^a-zA-Z0-9_-]/gu, "").slice(0, 40),
    left,
    right,
    width,
  })),
  scrollable: metrics.scrollable.map(({ tag, className, clientWidth, scrollWidth }) => ({
    tag,
    className: className.replace(/[^a-zA-Z0-9_-]/gu, "").slice(0, 40),
    clientWidth,
    scrollWidth,
  })),
});
export const noHorizontalOverflow = async (
  page: Pick<Page, "evaluate">,
  stage: LayoutStage,
): Promise<void> => {
  const metrics = await page.evaluate(collectLayoutMetrics);
  if (metrics.scrollWidth <= metrics.clientWidth + 1) {
    return;
  }
  const reportText = `${JSON.stringify(safeReport(stage, metrics))}\n`;
  let marker = `E2E_LAYOUT_${stage.toUpperCase()}_OVERFLOW`;
  try {
    const report = test.info().outputPath("layout.json");
    await writeFile(report, reportText, { mode: 0o600 });
  } catch {
    marker += "_REPORT_UNAVAILABLE";
  }
  throw new Error(marker);
};
