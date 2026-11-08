import { type Language } from "../src/presentation/preferences";
export type Dimension = {
  language: Language;
  mode: "light" | "dark";
  accent: "green" | "blue";
  width: number;
  zoom?: number;
};
/** Cover layout risks separately instead of multiplying every independent preference. */
export const dimensions = (engine: string): Dimension[] => {
  const shared: Dimension[] = [
    { width: 1280, language: "en", mode: "light", accent: "green" },
    { width: 320, language: "ar", mode: "dark", accent: "blue" },
  ];
  if (engine !== "chromium") {
    return shared;
  }
  return [
    ...shared,
    { width: 1280, language: "en", mode: "dark", accent: "blue" },
    { width: 320, language: "de", mode: "light", accent: "green" },
    { width: 320, language: "en-XA", mode: "light", accent: "green" },
    { width: 640, language: "en", mode: "light", accent: "green", zoom: 2 },
  ];
};
export const screenshotDimension = (dimension: Dimension): boolean =>
  dimension.zoom === undefined &&
  dimension.language !== "en-XA" &&
  (dimension.width <= 390 || (dimension.language === "en" && dimension.width === 1280));
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
      "reminders",
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

export const dimensionCaseTitle = (dimension: Dimension): string =>
  `${dimension.language} ${dimension.width}px ${dimension.accent} ${dimension.mode}${dimension.zoom === undefined ? "" : " at 200% zoom"}`;
