// @vitest-environment node
import { expect, it } from "vitest";
import {
  assertMatrixPlan,
  assertMatrixCounts,
  dimensions,
  dimensionCaseTitle,
  selectDiagnosticDimension,
  expectedDimensionCounts,
  factReadabilityContext,
  expectedEngineCounts,
  expectedRoutes,
  screenshotDimension,
  capturesFrame,
  type Dimension,
} from "../../e2e/design-matrix-plan";
const scenes = [
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
];
const captures = [
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
];
it.each([
  ["chromium", 14, 406, 420, 166],
  ["firefox", 2, 58, 60, 0],
  ["webkit", 2, 58, 60, 0],
] as const)(
  "pins representative %s inventory before dataset setup",
  (engine, count, loads, checks, shots) => {
    const plan = dimensions(engine);
    expect(new Set(plan.map((d) => JSON.stringify(d))).size).toBe(count);
    expect(expectedEngineCounts(engine)).toEqual({
      dimensions: count,
      pageLoads: loads,
      axeAndReflowChecks: checks,
      screenshots: shots,
    });
    expect(() => {
      assertMatrixPlan(engine);
    }).not.toThrow();
  },
);
it("covers all text languages and layout risks without a preference Cartesian product", () => {
  const plan = dimensions("chromium");
  expect([...new Set(plan.map((d) => d.language))].sort()).toEqual(
    ["en", "lv", "ar", "ko", "zh-Hans", "zh-Hant", "ja", "es", "de", "en-XA"].sort(),
  );
  for (const risk of [
    { width: 1280, language: "en", mode: "light", accent: "green" },
    { width: 320, language: "ar", mode: "dark", accent: "blue" },
    { width: 320, language: "en", mode: "dark", accent: "blue" },
    { width: 768, language: "en", mode: "dark", accent: "blue" },
    { width: 320, language: "de", mode: "light", accent: "green" },
    { width: 320, language: "en-XA", mode: "light", accent: "green" },
    { width: 640, language: "en", mode: "light", accent: "green", zoom: 2 },
  ]) {
    expect(plan).toContainEqual(risk);
  }
  for (const engine of ["firefox", "webkit"]) {
    expect(dimensions(engine)).toEqual([
      { width: 1280, language: "en", mode: "light", accent: "green" },
      { width: 320, language: "ar", mode: "dark", accent: "blue" },
    ]);
  }
});
it("pins all 29 scenes and the extra filter check independently", () => {
  expect(expectedRoutes).toEqual(scenes);
  expect(new Set(scenes).size).toBe(29);
  expect([...scenes, "cases-filter-panel"]).toHaveLength(30);
});
it("captures the documented narrow-language and desktop frames", () => {
  for (const d of dimensions("chromium")) {
    const wanted =
      d.language !== "en-XA" &&
      d.zoom === undefined &&
      (d.width <= 390 || (d.language === "en" && d.width === 1280));
    expect(screenshotDimension(d)).toBe(wanted);
    if (wanted) {
      const extra =
        d.language === "en" && d.width === 390 && d.mode === "light"
          ? ["cases-open-undecided"]
          : [];
      expect(scenes.filter((frame) => capturesFrame(frame, d)).sort()).toEqual(
        [...captures, ...extra].sort(),
      );
    }
  }
});
it.each([
  ["chromium", { dimensions: 50, pageLoads: 1450, axeAndReflowChecks: 1500, screenshots: 272 }],
  ["firefox", { dimensions: 8, pageLoads: 232, axeAndReflowChecks: 240, screenshots: 0 }],
  ["webkit", { dimensions: 8, pageLoads: 232, axeAndReflowChecks: 240, screenshots: 0 }],
] as const)("rejects obsolete Cartesian %s totals", (engine, old) => {
  expect(() => {
    assertMatrixCounts(engine, old);
  }).toThrow("independently pinned");
});
it("selects only the exact Native-owned diagnostic case and pins its partial counts", () => {
  const title = "en 640px green light at 200% zoom";
  const selected = selectDiagnosticDimension(dimensions("chromium"), title, "diagnostic");
  expect(selected).toEqual({ width: 640, language: "en", mode: "light", accent: "green", zoom: 2 });
  expect(dimensionCaseTitle(selected!)).toBe(title);
  expect(expectedDimensionCounts("chromium", selected!)).toEqual({
    dimensions: 1,
    pageLoads: 29,
    axeAndReflowChecks: 30,
    screenshots: 0,
  });
  expect(selectDiagnosticDimension(dimensions("chromium"), undefined, "complete")).toBeUndefined();
});
it("refuses unknown duplicate cross-engine and nondiagnostic case selectors", () => {
  const zoom: Dimension = { width: 640, language: "en", mode: "light", accent: "green", zoom: 2 };
  const title = "en 640px green light at 200% zoom";
  expect(() => selectDiagnosticDimension([zoom, zoom], title, "diagnostic")).toThrow("exactly one");
  expect(() => selectDiagnosticDimension(dimensions("firefox"), title, "diagnostic")).toThrow(
    "exactly one",
  );
  expect(() =>
    selectDiagnosticDimension(dimensions("chromium"), `${title} private`, "diagnostic"),
  ).toThrow("exactly one");
  expect(() => selectDiagnosticDimension(dimensions("chromium"), title, "complete")).toThrow(
    "diagnostic scope",
  );
  expect(() => selectDiagnosticDimension(dimensions("chromium"), "", "diagnostic")).toThrow(
    "exactly one",
  );
});
it("redacts every failing date into exact field frame and visibility metadata", () => {
  expect(
    factReadabilityContext("case-paid", [
      { field: "incidentDate", inert: false },
      { field: "paymentDate", inert: true },
    ]),
  ).toEqual({
    frame: "CASE_PAID",
    fields: [
      { field: "INCIDENT_DATE", inert: false },
      { field: "PAYMENT_DATE", inert: true },
    ],
    markers: [
      "E2E_FACT_READABILITY_INCIDENT_DATE_CASE_PAID_EXPOSED",
      "E2E_FACT_READABILITY_PAYMENT_DATE_CASE_PAID_INERT",
    ],
  });
});
it("rejects private appended or unknown readability identities", () => {
  expect(() =>
    factReadabilityContext("case-paid\nPRIVATE", [{ field: "paymentDate", inert: false }]),
  ).toThrow("INVALID_METADATA");
  expect(() =>
    factReadabilityContext("case-paid", [{ field: "paymentDate_PRIVATE", inert: false }]),
  ).toThrow("INVALID_METADATA");
  expect(() =>
    factReadabilityContext("case-paid", [{ field: "2026-11-04", inert: false }]),
  ).toThrow("INVALID_METADATA");
  expect(() => factReadabilityContext("case-paid", [])).toThrow("INVALID_METADATA");
});
