import { accents } from "../src/presentation/preferences";
export type Dimension = {
  language: "en" | "lv" | "ar" | "en-XA";
  mode: "light" | "dark";
  accent: "green" | "blue";
  width: number;
  zoom?: number;
};
export const dimensions = (engine: string): Dimension[] => {
  const result: Dimension[] = [];
  const widths = engine === "chromium" ? [320, 390, 768, 1280] : [320, 1280];
  const languages = engine === "chromium" ? (["en", "lv", "ar"] as const) : (["en", "ar"] as const);
  const themes = engine === "chromium" ? (["light", "dark"] as const) : (["light"] as const);
  for (const width of widths) {
    for (const language of languages) {
      for (const theme of themes) {
        for (const accent of accents) {
          result.push({ width, language, mode: theme, accent });
        }
      }
    }
  }
  if (engine === "chromium") {
    result.push(
      { width: 320, language: "en-XA", mode: "light", accent: "green" as const },
      { width: 640, language: "en", mode: "light", accent: "green" as const, zoom: 2 },
    );
  }
  return result;
};
export const expectedRoutes = [
  "cases-mine",
  "cases-notified",
  "cases-summary",
  "cases-open-undecided",
  "cases-decided-unpaid",
  "cases-recent",
  "cases-closed",
  "cases-voided",
  "cases-all",
  "cases-filtered",
  "case-undecided",
  "case-decided",
  "case-paid",
  "case-closed",
  "case-voided",
  "case-erased",
  "editor-open",
  "editor-decide",
  "editor-correct",
  "editor-void",
  "editor-work",
  "recovery",
  "paid-correction-review",
  "history-comparison",
  "operation-result",
  "authority-history",
  "not-found",
  "authority-list",
  "authority-detail",
] as const;
export const screenshotDimension = (dimension: Dimension): boolean =>
  dimension.zoom === undefined &&
  ((dimension.language === "en" && [390, 1280].includes(dimension.width)) ||
    (dimension.language === "en" && dimension.width === 320 && dimension.mode === "light") ||
    (["lv", "ar"].includes(dimension.language) && dimension.width === 390));
export const expectedCaptures = (engine: string, dimension: Dimension): number =>
  engine === "chromium" && screenshotDimension(dimension)
    ? 15 +
      Number(dimension.language === "en" && dimension.width === 390 && dimension.mode === "light")
    : 0;
export const capturesFrame = (frame: string, dimension: Dimension): boolean => {
  const baselinePair =
    frame === "cases-open-undecided" &&
    dimension.language === "en" &&
    dimension.width === 390 &&
    dimension.mode === "light";
  if (
    !baselinePair &&
    ![
      "cases-all",
      "cases-mine",
      "cases-summary",
      "case-decided",
      "case-voided",
      "case-erased",
      "editor-decide",
      "authority-list",
      "authority-detail",
      "authority-history",
      "not-found",
      "recovery",
      "editor-work",
      "paid-correction-review",
      "history-comparison",
    ].includes(frame)
  ) {
    return false;
  }
  return true;
};

export type MatrixCounts = {
  dimensions: number;
  pageLoads: number;
  axeAndReflowChecks: number;
  screenshots: number;
};
export const expectedEngineCounts = (engine: string): MatrixCounts =>
  engine === "chromium"
    ? { dimensions: 50, pageLoads: 1450, axeAndReflowChecks: 1500, screenshots: 272 }
    : { dimensions: 8, pageLoads: 232, axeAndReflowChecks: 240, screenshots: 0 };
export const assertMatrixCounts = (engine: string, actual: MatrixCounts): void => {
  const expected = expectedEngineCounts(engine);
  if (
    Object.keys(expected).some(
      (key) => actual[key as keyof MatrixCounts] !== expected[key as keyof MatrixCounts],
    )
  ) {
    throw new Error(
      "Design matrix counts do not match the independently pinned required inventory",
    );
  }
};
const validExtra = (engine: string, d: Dimension): boolean => {
  if (engine !== "chromium" || d.mode !== "light" || d.accent !== "green") {
    return false;
  }
  if (d.language === "en-XA") {
    return d.width === 320 && d.zoom === undefined;
  }
  return d.language === "en" && d.width === 640 && d.zoom === 2;
};
const validDimension = (engine: string, d: Dimension): boolean => {
  if (validExtra(engine, d)) {
    return true;
  }
  return (
    d.zoom === undefined &&
    (engine === "chromium" ? [320, 390, 768, 1280] : [320, 1280]).includes(d.width) &&
    (engine === "chromium" ? ["en", "lv", "ar"] : ["en", "ar"]).includes(d.language) &&
    (engine === "chromium" ? ["light", "dark"] : ["light"]).includes(d.mode) &&
    ["green", "blue"].includes(d.accent)
  );
};
export const assertMatrixPlan = (engine: string): void => {
  if (!["chromium", "firefox", "webkit"].includes(engine)) {
    throw new Error("Unknown design matrix engine");
  }
  const plan = dimensions(engine);
  if (
    plan.some((d) => !validDimension(engine, d)) ||
    new Set(plan.map((d) => JSON.stringify(d))).size !== plan.length ||
    new Set(expectedRoutes).size !== 29
  ) {
    throw new Error("Design matrix has invalid or duplicate required tuples or scenes");
  }
  const frames = [...expectedRoutes, "cases-filter-panel"];
  const screenshots = plan.reduce(
    (total, d) =>
      total +
      (engine === "chromium" && screenshotDimension(d)
        ? frames.filter((frame) => capturesFrame(frame, d)).length
        : 0),
    0,
  );
  if (
    plan.some(
      (d) =>
        expectedCaptures(engine, d) !==
        (engine === "chromium" && screenshotDimension(d)
          ? frames.filter((frame) => capturesFrame(frame, d)).length
          : 0),
    )
  ) {
    throw new Error("Design matrix capture whitelist does not match the per-dimension contract");
  }
  assertMatrixCounts(engine, {
    dimensions: plan.length,
    pageLoads: plan.length * expectedRoutes.length,
    axeAndReflowChecks: plan.length * frames.length,
    screenshots,
  });
};
