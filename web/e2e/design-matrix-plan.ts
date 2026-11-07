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
    { width: 390, language: "ja", mode: "dark", accent: "blue" },
    { width: 320, language: "de", mode: "light", accent: "green" },
    { width: 320, language: "en-XA", mode: "light", accent: "green" },
    { width: 640, language: "en", mode: "light", accent: "green", zoom: 2 },
  ];
};
const expectedRoutes = [
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

const factDateTokens = {
  incidentDate: "INCIDENT_DATE",
  incidentNotificationDate: "INCIDENT_NOTIFICATION_DATE",
  paymentDecisionDate: "PAYMENT_DECISION_DATE",
  paymentDate: "PAYMENT_DATE",
} as const;
/** Redaction deliberately accepts only static plan/frame identities and known date-field names. */
export const factReadabilityContext = (
  frame: string,
  failures: ReadonlyArray<{ field: string; inert: boolean }>,
) => {
  if (
    ![...expectedRoutes, "cases-filter-panel"].some((known) => known === frame) ||
    failures.length === 0 ||
    failures.some(
      (failure) =>
        !Object.hasOwn(factDateTokens, failure.field) || typeof failure.inert !== "boolean",
    )
  ) {
    throw new Error("E2E_FACT_READABILITY_INVALID_METADATA");
  }
  const frameToken = frame.replaceAll("-", "_").toUpperCase();
  const fields = failures.map((failure) => ({
    field: factDateTokens[failure.field as keyof typeof factDateTokens],
    inert: failure.inert,
  }));
  const markers = [
    ...new Set(
      fields.map(
        ({ field, inert }) =>
          `E2E_FACT_READABILITY_${field}_${frameToken}_${inert ? "INERT" : "EXPOSED"}`,
      ),
    ),
  ];
  return { frame: frameToken, fields, markers };
};
